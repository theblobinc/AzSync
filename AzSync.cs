using Oxide.Core;
using Oxide.Core.Database;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.SQLite.Libraries;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;

namespace Oxide.Plugins
{
    [Info("AzSync", "theblobinc", "1.0.0")]
    [Description("Asynchronously syncs economic and user data between Rust server and Azuriom website")]
    public class AzSync : CovalencePlugin
    {
        #region Configuration

        public class AzSyncConfig
        {
            [JsonProperty("Azuriom Settings")]
            public AzuriomSettings Azuriom { get; set; } = new AzuriomSettings();
        }

        public class AzuriomSettings
        {
            [JsonProperty("API Base URL")]
            public string BaseUrl { get; set; } = "https://yourazuriomsite.com/api/";

            [JsonProperty("Site Key (ApiToken)")]
            public string SiteKey { get; set; } = ""; // Site Key serves as the ApiToken

            [JsonProperty("API IP Address")]
            public string ApiIPAddress { get; set; } = "127.0.0.1"; // Default to localhost

            [JsonProperty("API Port")]
            public int ApiPort { get; set; } = 8181; // Default port (e.g., 8181)
        }

        public class TransactionRecord
        {
            [JsonProperty("timestamp")]
            public string Timestamp { get; set; } // ISO 8601 format

            [JsonProperty("transaction_type")]
            public string TransactionType { get; set; } // "deposit", "withdrawal", "transfer", etc.

            [JsonProperty("steam_id")]
            public string SteamId { get; set; } // Player initiating the transaction

            [JsonProperty("target_steam_id")]
            public string TargetSteamId { get; set; } // Player receiving funds (for transfers)

            [JsonProperty("amount")]
            public double Amount { get; set; }
        }

        public static class QueryStringParser
        {
            public static Dictionary<string, string> Parse(string query)
            {
                var queryDictionary = new Dictionary<string, string>();

                if (string.IsNullOrWhiteSpace(query))
                    return queryDictionary;

                // Remove the leading '?' if present
                if (query.StartsWith("?"))
                    query = query.Substring(1);

                var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

                foreach (var pair in pairs)
                {
                    var kvp = pair.Split('=', 2);
                    if (kvp.Length == 2)
                    {
                        var key = Uri.UnescapeDataString(kvp[0]);
                        var value = Uri.UnescapeDataString(kvp[1]);
                        queryDictionary[key] = value;
                    }
                    else if (kvp.Length == 1)
                    {
                        var key = Uri.UnescapeDataString(kvp[0]);
                        queryDictionary[key] = string.Empty;
                    }
                }

                return queryDictionary;
            }
        }

        private AzSyncConfig pluginConfig;

        protected override void LoadDefaultConfig()
        {
            Puts("Creating a new configuration file.");
            pluginConfig = new AzSyncConfig();
            SaveConfig();
        }

        private void LoadConfigData()
        {
            pluginConfig = Config.ReadObject<AzSyncConfig>();
            if (pluginConfig == null)
            {
                Puts("Could not load config, creating new default config.");
                pluginConfig = new AzSyncConfig();
                SaveConfig();
            }

            // Validate essential configurations
            if (string.IsNullOrEmpty(pluginConfig.Azuriom.BaseUrl))
            {
                Puts("Azuriom BaseUrl is not set. Please configure it in the plugin config.");
            }

            if (string.IsNullOrEmpty(pluginConfig.Azuriom.SiteKey))
            {
                Puts("Azuriom Site Key is not set. Please configure it in the plugin config.");
            }

            if (string.IsNullOrEmpty(pluginConfig.Azuriom.ApiIPAddress))
            {
                Puts("API IP Address is not set. Defaulting to 127.0.0.1.");
                pluginConfig.Azuriom.ApiIPAddress = "127.0.0.1";
                SaveConfig();
            }

            if (pluginConfig.Azuriom.ApiPort <= 0 || pluginConfig.Azuriom.ApiPort > 65535)
            {
                Puts($"Invalid API Port '{pluginConfig.Azuriom.ApiPort}' in config. Defaulting to 8181.");
                pluginConfig.Azuriom.ApiPort = 8181;
                SaveConfig();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(pluginConfig, true);
        }

        #endregion

        #region BalanceOperation Enum

        /// <summary>
        /// Enum representing the type of balance operation.
        /// </summary>
        public enum BalanceOperation
        {
            Add,
            Subtract,
            Set
        }

        #endregion

        #region Fields and Properties

        private Plugin EconomicsPlugin;
        private bool isSyncing = false;

        private HttpListener _httpListener;

        private Core.SQLite.Libraries.SQLite Sqlite;
        private Connection Sqlite_conn;

        private Dictionary<(string path, string method), Action<HttpListenerRequest, Action<string>>> routeTable;

        // Lock object for thread safety
        private readonly object balanceLock = new object();

        // **Fields for Ledger Management**
        private string dbPath;

        // Timer for consistency checks
        private Timer consistencyTimer;

        #endregion

        #region Initialization

        private void Init()
        {
            LoadConfigData();

            // Define the path to the SQLite database file
            dbPath = Path.Combine(Interface.Oxide.DataDirectory, "azsync-transactions.db");

            // Retrieve the Sqlite library from Oxide
            Sqlite = Interface.Oxide.GetLibrary<Core.SQLite.Libraries.SQLite>();

            // Open the database connection
            Sqlite_conn = Sqlite.OpenDb(dbPath, this);

            if (Sqlite_conn == null)
            {
                Puts("Failed to open SQLite database connection.");
                return;
            }

            // Initialize the SQLite ledger database
            InitializeDatabase();

            // **Reordered: Attempt to load the Economics plugin before loading balances**
            EconomicsPlugin = plugins.Find("Economics");
            if (EconomicsPlugin == null)
            {
                Puts("Economics plugin not found. AzSync requires the Economics plugin to function.");
                return;
            }
            else
            {
                Puts("Economics plugin found and loaded successfully.");
            }

            // Load Economics plugin balances into SQLite
            LoadEconomicsBalancesToDatabase();

            // Initialize the route table
            InitializeRouteTable();

            // Start initial synchronization from website to in-game balances
            FetchAndSyncBalancesFromWebsiteAsync();

            // Start HTTP Listener for receiving data from Azuriom
            StartHttpListener();

            // Start the consistency verification timer (e.g., every 5 minutes)
            consistencyTimer = timer.Repeat(300, 0, VerifyConsistency);
            Puts("Started consistency verification timer.");

            Puts("AzSync plugin initialized.");
        }

        private void Unload()
        {
            StopHttpListener();

            if (Sqlite_conn != null)
            {
                Sqlite.CloseDb(Sqlite_conn);
                Puts("SQLite connection closed.");
            }

            // Destroy the consistency timer
            if (consistencyTimer != null)
            {
                consistencyTimer.Destroy();
                Puts("Consistency verification timer stopped.");
            }

            Puts("AzSync plugin unloaded.");
        }

        #endregion

        #region SQLite Ledger Management

        /// <summary>
        /// Initializes the SQLite database and creates the necessary tables if they don't exist.
        /// </summary>
        private void InitializeDatabase()
        {
            var createTransactionsTableSql = @"
                CREATE TABLE IF NOT EXISTS transactions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp TEXT NOT NULL,
                    transaction_type TEXT NOT NULL,
                    steam_id TEXT NOT NULL,
                    target_steam_id TEXT,
                    amount REAL NOT NULL
                );";

            var createUserBalancesTableSql = @"
                CREATE TABLE IF NOT EXISTS user_balances (
                    steam_id TEXT PRIMARY KEY,
                    balance REAL NOT NULL
                );";

            var createUserAccountsTableSql = @"
                CREATE TABLE IF NOT EXISTS user_accounts (
                    steam_id TEXT PRIMARY KEY
                    -- Add other relevant fields if necessary
                );";

            try
            {
                // Create transactions table
                Sqlite.ExecuteNonQuery(Core.Database.Sql.Builder.Append(createTransactionsTableSql), Sqlite_conn, (rows) =>
                {
                    Puts("SQLite transactions table ensured.");
                });

                // Create user_balances table
                Sqlite.ExecuteNonQuery(Core.Database.Sql.Builder.Append(createUserBalancesTableSql), Sqlite_conn, (rows) =>
                {
                    Puts("SQLite user_balances table ensured.");
                });

                // Create user_accounts table
                Sqlite.ExecuteNonQuery(Core.Database.Sql.Builder.Append(createUserAccountsTableSql), Sqlite_conn, (rows) =>
                {
                    Puts("SQLite user_accounts table ensured.");
                });
            }
            catch (Exception ex)
            {
                Puts($"Error creating tables: {ex.Message}");
            }
        }

        /// <summary>
        /// Inserts a new transaction record into the SQLite database.
        /// </summary>
        /// <param name="transaction">The transaction record to insert.</param>
        private void InsertTransaction(TransactionRecord transaction)
        {
            var sql = Core.Database.Sql.Builder.Append(
                "INSERT INTO transactions (timestamp, transaction_type, steam_id, target_steam_id, amount) VALUES (@0, @1, @2, @3, @4)",
                transaction.Timestamp,
                transaction.TransactionType,
                transaction.SteamId,
                transaction.TargetSteamId,
                transaction.Amount);

            try
            {
                Sqlite.Insert(sql, Sqlite_conn, rowsAffected =>
                {
                    if (rowsAffected > 0)
                    {
                        Puts($"Transaction inserted: {transaction.TransactionType} of {transaction.Amount} by {transaction.SteamId}" +
                             (string.IsNullOrEmpty(transaction.TargetSteamId) ? "" : $" to {transaction.TargetSteamId}"));
                    }
                    else
                    {
                        Puts("Failed to insert transaction.");
                    }
                });
            }
            catch (Exception ex)
            {
                Puts($"Error inserting transaction: {ex.Message}");
            }
        }

        /// <summary>
        /// Inserts or updates a user's balance in the SQLite database.
        /// </summary>
        /// <param name="steamId">The player's SteamID.</param>
        /// <param name="balance">The new balance amount.</param>
        /// <returns>True if the operation was successful; otherwise, false.</returns>
        private bool UpsertUserBalance(string steamId, double balance)
        {
            var sql = Core.Database.Sql.Builder.Append(@"
                INSERT INTO user_balances (steam_id, balance) 
                VALUES (@0, @1)
                ON CONFLICT(steam_id) DO UPDATE SET balance = @1;",
                steamId, balance);

            try
            {
                Sqlite.Insert(sql, Sqlite_conn);
                Puts($"Upserted balance for SteamID: {steamId} to {balance}.");
                return true;
            }
            catch (Exception ex)
            {
                Puts($"Error upserting balance for SteamID {steamId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Retrieves all player SteamIDs from the SQLite database.
        /// </summary>
        /// <param name="callback">The callback to execute with the results.</param>
        private void GetAllPlayerSteamIds(Action<List<string>> callback)
        {
            var sql = Core.Database.Sql.Builder.Append("SELECT steam_id FROM user_balances");
            Sqlite.Query(sql, Sqlite_conn, list =>
            {
                var steamIds = new List<string>();
                foreach (var record in list)
                {
                    if (record.ContainsKey("steam_id"))
                    {
                        steamIds.Add(record["steam_id"].ToString());
                    }
                }
                callback(steamIds);
            });
        }

        /// <summary>
        /// Retrieves a user's balance from the SQLite database.
        /// </summary>
        private void GetDatabaseBalance(string steamId, Action<double> callback)
        {
            var sql = Core.Database.Sql.Builder.Append("SELECT balance FROM user_balances WHERE steam_id = @0", steamId);
            Sqlite.Query(sql, Sqlite_conn, list =>
            {
                double balance = 0.0;

                if (list != null && list.Count > 0)
                {
                    var record = list[0];
                    if (record.ContainsKey("balance") && double.TryParse(record["balance"].ToString(), out double dbBalance))
                    {
                        balance = dbBalance;
                    }
                }

                callback(balance);
            });
        }

        /// <summary>
        /// Adds a new user to the user_balances table.
        /// </summary>
        private void AddUserToDatabase(string steamId, double initialBalance = 0.0)
        {
            var sql = Core.Database.Sql.Builder.Append(@"
                INSERT INTO user_balances (steam_id, balance) 
                VALUES (@0, @1)
                ON CONFLICT(steam_id) DO NOTHING;",
                steamId, initialBalance);

            try
            {
                Sqlite.Insert(sql, Sqlite_conn);
                Puts($"Added user to database: SteamID {steamId} with balance {initialBalance}.");
            }
            catch (Exception ex)
            {
                Puts($"Error adding user {steamId} to database: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a user exists in the user_balances table.
        /// </summary>
        /// <param name="steamId">The SteamID to check for.</param>
        /// <param name="callback">The callback to execute with the result.</param>
        private void UserExistsInDatabase(string steamId, Action<bool> callback)
        {
            var sql = Core.Database.Sql.Builder.Append("SELECT COUNT(*) AS count FROM user_balances WHERE steam_id = @0", steamId);
            Sqlite.Query(sql, Sqlite_conn, list =>
            {
                bool exists = false;
                if (list != null && list.Count > 0)
                {
                    var record = list[0];
                    if (record.ContainsKey("count") && int.TryParse(record["count"].ToString(), out int count))
                    {
                        exists = count > 0;
                    }
                }
                callback(exists);
            });
        }

        /// <summary>
        /// Asynchronously retrieves transactions from the SQLite database based on optional time range filters and pagination.
        /// </summary>
        private void GetTransactions(DateTime? startTime, DateTime? endTime, int limit, int offset, Action<List<TransactionRecord>> callback)
        {
            var sqlQuery = "SELECT timestamp, transaction_type, steam_id, target_steam_id, amount FROM transactions WHERE 1=1";
            var parameters = new List<object>();
            int paramIndex = 0;

            if (startTime.HasValue)
            {
                sqlQuery += $" AND timestamp >= @{paramIndex}";
                parameters.Add(startTime.Value.ToString("o")); // ISO 8601 format
                paramIndex++;
            }

            if (endTime.HasValue)
            {
                sqlQuery += $" AND timestamp <= @{paramIndex}";
                parameters.Add(endTime.Value.ToString("o")); // ISO 8601 format
                paramIndex++;
            }

            sqlQuery += $" ORDER BY timestamp DESC LIMIT @{paramIndex} OFFSET @{paramIndex + 1};";
            parameters.Add(limit);
            parameters.Add(offset);

            var sql = Core.Database.Sql.Builder.Append(sqlQuery, parameters.ToArray());

            Sqlite.Query(sql, Sqlite_conn, list =>
            {
                if (list == null || list.Count == 0)
                {
                    Puts("No transactions found or an error occurred while fetching transactions.");
                    callback(new List<TransactionRecord>());
                    return;
                }

                var results = new List<TransactionRecord>();
                foreach (var dict in list)
                {
                    var record = new TransactionRecord
                    {
                        Timestamp = dict["timestamp"].ToString(),
                        TransactionType = dict["transaction_type"].ToString(),
                        SteamId = dict["steam_id"].ToString(),
                        TargetSteamId = dict.ContainsKey("target_steam_id") ? dict["target_steam_id"]?.ToString() : "",
                        Amount = Convert.ToDouble(dict["amount"])
                    };
                    results.Add(record);
                }

                callback(results);
            });
        }

        /// <summary>
        /// Loads Economics plugin balances into SQLite database.
        /// </summary>
        private void LoadEconomicsBalancesToDatabase()
        {
            Puts("Loading balances from Economics plugin to SQLite database.");

            if (EconomicsPlugin == null)
            {
                Puts("EconomicsPlugin is null. Cannot load balances.");
                return;
            }

            // Retrieve all players from the Economics plugin
            var allPlayers = EconomicsPlugin.Call("GetAllPlayers") as List<IPlayer>;

            if (allPlayers == null)
            {
                Puts("EconomicsPlugin returned null or invalid data for GetAllPlayers.");
                return;
            }

            foreach (var player in allPlayers)
            {
                if (player == null)
                {
                    Puts("Encountered a null player in EconomicsPlugin.GetAllPlayers(). Skipping.");
                    continue;
                }

                string steamId = player.Id;
                object balanceObj = EconomicsPlugin.Call("GetBalance", player);
                double balance = balanceObj is double b ? b : 0.0;

                // Upsert the user's balance into the SQLite database
                UpsertUserBalance(steamId, balance);
                Puts($"Loaded balance for SteamID: {steamId} with amount: {balance}.");
            }

            Puts("Completed loading Economics balances into SQLite database.");
        }

        #endregion

        #region HTTP Listener

        private void StartHttpListener()
        {
            try
            {
                _httpListener = new HttpListener();

                // Retrieve the configured IP address and port
                string apiIp = pluginConfig.Azuriom.ApiIPAddress;
                int apiPort = pluginConfig.Azuriom.ApiPort;

                // Validate IP address format
                if (!IPAddress.TryParse(apiIp, out IPAddress ipAddress))
                {
                    Puts($"Invalid API IP Address '{apiIp}' in config. Defaulting to 127.0.0.1.");
                    apiIp = "127.0.0.1";
                }

                // Validate port number
                if (apiPort <= 0 || apiPort > 65535)
                {
                    Puts($"Invalid API Port '{apiPort}' in config. Defaulting to 8181.");
                    apiPort = 8181;
                }

                // Construct the prefix using the configured IP address and port
                string prefix = $"http://{apiIp}:{apiPort}/azsync/";

                _httpListener.Prefixes.Add(prefix);
                _httpListener.Start();
                _httpListener.BeginGetContext(OnRequestReceived, null);
                Puts($"HTTP listener started on {prefix}");
            }
            catch (HttpListenerException hlex)
            {
                Puts($"Failed to start HTTP listener: {hlex.Message}");
            }
            catch (Exception ex)
            {
                Puts($"Unexpected error starting HTTP listener: {ex.Message}");
            }
        }

        private void StopHttpListener()
        {
            try
            {
                if (_httpListener != null)
                {
                    _httpListener.Stop();
                    _httpListener.Close();
                    _httpListener = null;
                    Puts("HTTP listener stopped.");
                }
            }
            catch (Exception ex)
            {
                Puts($"Error stopping HTTP listener: {ex.Message}");
            }
        }

        private void InitializeRouteTable()
        {
            routeTable = new Dictionary<(string path, string method), Action<HttpListenerRequest, Action<string>>>()
            {
                { ("/azsync/set-balance", "POST"), HandleBalanceUpdateRequest },
                { ("/azsync/get-balance", "GET"), HandleGetBalanceRequest },
                { ("/azsync/user-check", "GET"), HandleUserCheckRequest },
                { ("/azsync/reset-balance", "POST"), HandleResetBalanceRequest },
                { ("/azsync/get-transactions", "GET"), HandleGetTransactions },
                { ("/azsync/add-user", "POST"), HandleAddUserRequest },
                { ("/azsync/add-balance", "POST"), HandleAddBalanceRequest },
                { ("/azsync/subtract-balance", "POST"), HandleSubtractBalanceRequest },
                { ("/azsync/get-all-balances", "GET"), HandleGetAllBalancesRequest }
                // Add more routes here as needed
            };
        }

        private void OnRequestReceived(IAsyncResult result)
        {
            if (_httpListener == null || !_httpListener.IsListening) return;

            HttpListenerContext context = null;
            try
            {
                context = _httpListener.EndGetContext(result);
            }
            catch (Exception)
            {
                // Handle if needed
                return;
            }
            finally
            {
                if (_httpListener.IsListening)
                {
                    _httpListener.BeginGetContext(OnRequestReceived, null);
                }
            }

            var request = context.Request;
            var response = context.Response;

            // API key verification
            if (request.Headers["Azuriom-Link-Token"] == null ||
                !request.Headers["Azuriom-Link-Token"].Equals(pluginConfig.Azuriom.SiteKey))
            {
                Puts($"Unauthorized access attempt from {request.RemoteEndPoint}");
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                string unauthorizedResponse = "{ \"error\": \"Unauthorized access\" }";
                SendResponseAsync(response, unauthorizedResponse);
                return;
            }

            // Route the request
            var path = request.Url.AbsolutePath.ToLower();
            var method = request.HttpMethod.ToUpper();

            try
            {
                var key = (path, method);
                if (routeTable.ContainsKey(key))
                {
                    routeTable[key](request, responseString =>
                    {
                        // Send the response
                        SendResponseAsync(response, responseString);
                    });
                }
                else
                {
                    // Endpoint not found
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    string responseString = "{ \"error\": \"Endpoint not found\" }";
                    SendResponseAsync(response, responseString);
                }
            }
            catch (Exception ex)
            {
                Puts($"Error processing request: {ex.Message}");
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                string responseString = "{ \"error\": \"Internal server error\" }";
                SendResponseAsync(response, responseString);
            }
        }

        private async void SendResponseAsync(HttpListenerResponse response, string responseString)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            response.ContentType = "application/json";
            try
            {
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (Exception)
            {
                // Handle exception if needed
            }
            finally
            {
                response.Close();
            }
        }

        // Route handlers

        private void HandleBalanceUpdateRequest(HttpListenerRequest request, Action<string> responseCallback)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                string body = reader.ReadToEnd();
                var balanceData = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);
                string responseString = HandleBalanceUpdate(balanceData);
                responseCallback(responseString);
            }
        }

        /// <summary>
        /// Handles the ResetBalance HTTP request to reset a user's balance.
        /// </summary>
        private void HandleResetBalanceRequest(HttpListenerRequest request, Action<string> responseCallback)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                string body = reader.ReadToEnd();
                var resetData = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);

                // Validate presence of "game_id"
                if (!resetData.ContainsKey("game_id") || string.IsNullOrWhiteSpace(resetData["game_id"]))
                {
                    Puts("ResetBalance request missing or has empty 'game_id'.");
                    responseCallback("{ \"error\": \"Missing or empty 'game_id' in request body.\" }");
                    return;
                }

                string steamId = resetData["game_id"];

                // Reset balance to zero using 'Set' operation
                bool setSuccess = SetEconomicsBalance(steamId, 0.0);
                if (!setSuccess)
                {
                    Puts($"Failed to reset balance for SteamID: {steamId} via Economics plugin.");
                    responseCallback("{ \"error\": \"Failed to reset balance via Economics plugin.\" }");
                    return;
                }

                // Find the player in-game
                var player = covalence.Players.FindPlayerById(steamId);
                if (player != null)
                {
                    Puts($"Reset balance for {steamId} to 0.");

                    // Send the reset balance to the website
                    SendBalanceToWebsite(player, 0.0, "set", () =>
                    {
                        // Sync the balance from the website to confirm
                        SyncPlayerBalanceWithWebsite(steamId);
                    });

                    // Add to Transaction Ledger
                    AddTransactionToLedger("reset_balance", steamId, 0.0, "Balance reset via API");

                    responseCallback("{ \"status\": \"Balance reset successfully.\" }");
                }
                else
                {
                    // Player is offline; balance is set in local storage only
                    Puts($"Player {steamId} is offline. Balance reset to 0 in database.");

                    // Even if the player is offline, send the balance to website and sync
                    SendBalanceToWebsite(null, 0.0, "set", () =>
                    {
                        SyncPlayerBalanceWithWebsite(steamId);
                    });

                    // Add to Transaction Ledger
                    AddTransactionToLedger("reset_balance_offline", steamId, 0.0, "Balance reset via API while offline");

                    responseCallback("{ \"status\": \"Balance reset successfully.\" }");
                }
            }
        }

        private void HandleGetBalanceRequest(HttpListenerRequest request, Action<string> responseCallback)
        {
            var queryParams = QueryStringParser.Parse(request.Url.Query);

            // Validate presence of "game_id"
            if (!queryParams.ContainsKey("game_id") || string.IsNullOrWhiteSpace(queryParams["game_id"]))
            {
                Puts("GetBalance request missing or has empty 'game_id'.");
                responseCallback("{ \"error\": \"Missing or empty 'game_id' in query parameters.\" }");
                return;
            }

            string steamId = queryParams["game_id"];

            // Retrieve balance from SQLite database asynchronously
            GetDatabaseBalance(steamId, balance =>
            {
                Puts($"Retrieved balance for SteamID: {steamId} - Balance: {balance}");
                string jsonResponse = $"{{ \"steam_id\": \"{steamId}\", \"balance\": {balance} }}";
                responseCallback(jsonResponse);
            });
        }

        private void HandleUserCheckRequest(HttpListenerRequest request, Action<string> responseCallback)
        {
            HandleUserCheck(responseCallback);
        }

        private void HandleGetTransactions(HttpListenerRequest request, Action<string> responseCallback)
        {
            var queryParams = QueryStringParser.Parse(request.Url.Query);

            DateTime? startTime = null;
            DateTime? endTime = null;
            int limit = 100; // Default limit
            int offset = 0;  // Default offset

            // Parse 'start_time' if provided
            if (queryParams.ContainsKey("start_time") && !string.IsNullOrWhiteSpace(queryParams["start_time"]))
            {
                if (DateTime.TryParse(queryParams["start_time"], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsedStart))
                {
                    startTime = parsedStart.ToUniversalTime();
                }
                else
                {
                    Puts($"Invalid 'start_time' format received: {queryParams["start_time"]}");
                    responseCallback("{ \"error\": \"Invalid 'start_time' format. Use ISO 8601 format.\" }");
                    return;
                }
            }

            // Parse 'end_time' if provided
            if (queryParams.ContainsKey("end_time") && !string.IsNullOrWhiteSpace(queryParams["end_time"]))
            {
                if (DateTime.TryParse(queryParams["end_time"], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsedEnd))
                {
                    endTime = parsedEnd.ToUniversalTime();
                }
                else
                {
                    Puts($"Invalid 'end_time' format received: {queryParams["end_time"]}");
                    responseCallback("{ \"error\": \"Invalid 'end_time' format. Use ISO 8601 format.\" }");
                    return;
                }
            }

            // Parse 'limit' if provided
            if (queryParams.ContainsKey("limit") && !string.IsNullOrWhiteSpace(queryParams["limit"]))
            {
                if (int.TryParse(queryParams["limit"], out int parsedLimit))
                {
                    if (parsedLimit > 0 && parsedLimit <= 1000)
                    {
                        limit = parsedLimit;
                    }
                    else
                    {
                        Puts($"Invalid 'limit' value received: {queryParams["limit"]}. Must be between 1 and 1000.");
                        responseCallback("{ \"error\": \"Invalid 'limit' value. Must be between 1 and 1000.\" }");
                        return;
                    }
                }
                else
                {
                    Puts($"Invalid 'limit' format received: {queryParams["limit"]}");
                    responseCallback("{ \"error\": \"Invalid 'limit' format. Must be an integer.\" }");
                    return;
                }
            }

            // Parse 'offset' if provided
            if (queryParams.ContainsKey("offset") && !string.IsNullOrWhiteSpace(queryParams["offset"]))
            {
                if (int.TryParse(queryParams["offset"], out int parsedOffset))
                {
                    if (parsedOffset >= 0)
                    {
                        offset = parsedOffset;
                    }
                    else
                    {
                        Puts($"Invalid 'offset' value received: {queryParams["offset"]}. Must be 0 or greater.");
                        responseCallback("{ \"error\": \"Invalid 'offset' value. Must be 0 or greater.\" }");
                        return;
                    }
                }
                else
                {
                    Puts($"Invalid 'offset' format received: {queryParams["offset"]}");
                    responseCallback("{ \"error\": \"Invalid 'offset' format. Must be an integer.\" }");
                    return;
                }
            }

            // Retrieve transactions from SQLite database asynchronously
            lock (balanceLock) // Ensure thread-safe access
            {
                GetTransactions(startTime, endTime, limit, offset, filteredTransactions =>
                {
                    // Serialize the filtered transactions to JSON
                    string jsonResponse = JsonConvert.SerializeObject(filteredTransactions, Formatting.Indented);

                    Puts($"Fetched {filteredTransactions.Count} transaction(s) from the ledger between " +
                         $"{(startTime.HasValue ? startTime.Value.ToString("o") : "beginning")} and " +
                         $"{(endTime.HasValue ? endTime.Value.ToString("o") : "now")} with limit {limit} and offset {offset}.");

                    responseCallback(jsonResponse);
                });
            }
        }

        // New route handlers

        /// <summary>
        /// Handles the AddUser HTTP request to add a new user.
        /// </summary>
        private void HandleAddUserRequest(HttpListenerRequest request, Action<string> responseCallback)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                string body = reader.ReadToEnd();
                var userData = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);

                // Validate required fields
                if (!userData.ContainsKey("steam_id"))
                {
                    Puts("AddUser request missing 'steam_id'.");
                    responseCallback("{ \"error\": \"Missing 'steam_id' in request body.\" }");
                    return;
                }

                string steamId = userData["steam_id"];

                // Optional: Initial balance
                double initialBalance = 0.0;
                if (userData.ContainsKey("balance"))
                {
                    if (!double.TryParse(userData["balance"], out initialBalance))
                    {
                        Puts($"Invalid 'balance' value received: {userData["balance"]}");
                        responseCallback("{ \"error\": \"Invalid 'balance' value. Must be a number.\" }");
                        return;
                    }
                }

                // Check if user already exists in the database
                UserExistsInDatabase(steamId, exists =>
                {
                    if (exists)
                    {
                        Puts($"User {steamId} already exists in the database.");
                        responseCallback("{ \"error\": \"User already exists.\" }");
                        return;
                    }

                    // Add user to SQLite database
                    UpsertUserBalance(steamId, initialBalance);
                    Puts($"Added user {steamId} with balance {initialBalance} to the database.");

                    // Record the addition in the transaction ledger
                    AddTransactionToLedger("add_user", steamId, initialBalance, "User added via API");

                    responseCallback("{ \"status\": \"User added successfully.\" }");
                });
            }
        }

        /// <summary>
        /// Handles the AddBalance HTTP request to add balance to an existing user.
        /// </summary>
        private void HandleAddBalanceRequest(HttpListenerRequest request, Action<string> responseCallback)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                string body = reader.ReadToEnd();
                var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);

                // Validate presence of "steam_id" and "amount"
                if (!data.ContainsKey("steam_id") || !data.ContainsKey("amount"))
                {
                    Puts("AddBalance request missing 'steam_id' or 'amount'.");
                    responseCallback("{ \"error\": \"Missing 'steam_id' or 'amount' in request body.\" }");
                    return;
                }

                string steamId = data["steam_id"];
                if (!double.TryParse(data["amount"], out double amount) || amount <= 0)
                {
                    Puts($"Invalid 'amount' value received: {data["amount"]}");
                    responseCallback("{ \"error\": \"Invalid 'amount' value. Must be a positive number.\" }");
                    return;
                }

                // Check if user exists in the database
                UserExistsInDatabase(steamId, exists =>
                {
                    if (!exists)
                    {
                        Puts($"User {steamId} does not exist in the database.");
                        responseCallback("{ \"error\": \"User does not exist.\" }");
                        return;
                    }

                    // Retrieve current balance from the database
                    GetDatabaseBalance(steamId, currentBalance =>
                    {
                        double newBalance = currentBalance + amount;

                        // Update the balance in the SQLite database
                        UpsertUserBalance(steamId, newBalance);
                        Puts($"Added {amount} to balance for SteamID: {steamId}. New balance: {newBalance}.");

                        // Find if the user is online to update in-game balance
                        var player = covalence.Players.FindPlayerById(steamId);
                        if (player != null)
                        {
                            bool economicsSuccess = SetEconomicsBalance(steamId, newBalance);
                            if (economicsSuccess)
                            {
                                Puts($"Added {amount} to in-game balance for {steamId}.");
                                SendBalanceToWebsite(player, amount, "deposit");
                            }
                            else
                            {
                                Puts($"Failed to add balance via Economics plugin for {steamId}.");
                                responseCallback("{ \"error\": \"Failed to add balance via Economics plugin.\" }");
                                return;
                            }
                        }
                        else
                        {
                            Puts($"User {steamId} is offline. Balance added to database only.");
                        }

                        // Record the addition in the transaction ledger
                        AddTransactionToLedger("add_balance", steamId, amount, "Balance added via API");

                        responseCallback("{ \"status\": \"Balance added successfully.\" }");
                    });
                });
            }
        }

        private void HandleGetAllBalancesRequest(HttpListenerRequest request, Action<string> responseCallback)
        {
            GetAllPlayerBalances(allBalances =>
            {
                string jsonResponse = JsonConvert.SerializeObject(allBalances, Formatting.Indented);
                responseCallback(jsonResponse);
            });
        }

        /// <summary>
        /// Handles the SubtractBalance HTTP request to subtract balance from an existing user.
        /// </summary>
        private void HandleSubtractBalanceRequest(HttpListenerRequest request, Action<string> responseCallback)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                string body = reader.ReadToEnd();
                var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);

                // Validate presence of "steam_id" and "amount"
                if (!data.ContainsKey("steam_id") || !data.ContainsKey("amount"))
                {
                    Puts("SubtractBalance request missing 'steam_id' or 'amount'.");
                    responseCallback("{ \"error\": \"Missing 'steam_id' or 'amount' in request body.\" }");
                    return;
                }

                string steamId = data["steam_id"];
                if (!double.TryParse(data["amount"], out double amount) || amount <= 0)
                {
                    Puts($"Invalid 'amount' value received: {data["amount"]}");
                    responseCallback("{ \"error\": \"Invalid 'amount' value. Must be a positive number.\" }");
                    return;
                }

                // Check if user exists in the database
                UserExistsInDatabase(steamId, exists =>
                {
                    if (!exists)
                    {
                        Puts($"User {steamId} does not exist in the database.");
                        responseCallback("{ \"error\": \"User does not exist.\" }");
                        return;
                    }

                    // Retrieve current balance from the database
                    GetDatabaseBalance(steamId, currentBalance =>
                    {
                        if (currentBalance < amount)
                        {
                            Puts($"User {steamId} has insufficient balance. Current: {currentBalance}, Attempted to subtract: {amount}");
                            responseCallback("{ \"error\": \"Insufficient balance.\" }");
                            return;
                        }

                        double newBalance = currentBalance - amount;

                        // Update the balance in the SQLite database
                        UpsertUserBalance(steamId, newBalance);
                        Puts($"Subtracted {amount} from balance for SteamID: {steamId}. New balance: {newBalance}.");

                        // Find if the user is online to update in-game balance
                        var player = covalence.Players.FindPlayerById(steamId);
                        if (player != null)
                        {
                            bool economicsSuccess = SetEconomicsBalance(steamId, newBalance);
                            if (economicsSuccess)
                            {
                                Puts($"Subtracted {amount} from in-game balance for {steamId}.");
                                SendBalanceToWebsite(player, amount, "withdrawal");
                            }
                            else
                            {
                                Puts($"Failed to subtract balance via Economics plugin for {steamId}.");
                                responseCallback("{ \"error\": \"Failed to subtract balance via Economics plugin.\" }");
                                return;
                            }
                        }
                        else
                        {
                            Puts($"User {steamId} is offline. Balance subtracted from database only.");
                        }

                        // Record the subtraction in the transaction ledger
                        AddTransactionToLedger("subtract_balance", steamId, amount, "Balance subtracted via API");

                        responseCallback("{ \"status\": \"Balance subtracted successfully.\" }");
                    });
                });
            }
        }

        #endregion

        #region Handling Incoming Data

        private string HandleBalanceUpdate(Dictionary<string, string> balanceData)
        {
            // Validate presence of "game_id"
            if (!balanceData.ContainsKey("game_id"))
            {
                Puts("Balance update request missing 'game_id'.");
                return "{ \"error\": \"Missing 'game_id' in balance data.\" }";
            }

            string steamId = balanceData["game_id"];

            // Validate presence of "balance"
            if (balanceData.ContainsKey("balance"))
            {
                if (double.TryParse(balanceData["balance"], out double newBalance))
                {
                    Puts($"Received balance update for SteamID: {steamId} with new balance: {newBalance}");

                    // Updated call with callback
                    SetPlayerBalance(steamId, newBalance, BalanceOperation.Set, success =>
                    {
                        if (success)
                        {
                            Puts($"Successfully set balance for SteamID: {steamId} to {newBalance}.");
                        }
                        else
                        {
                            Puts($"Failed to set balance for SteamID: {steamId}.");
                        }
                    });

                    // Find the player in-game
                    var player = covalence.Players.FindPlayerById(steamId);
                    if (player != null)
                    {
                        double currentBalance = GetEconomicsBalance(steamId);
                        if (Math.Abs(currentBalance - newBalance) > 0.01)
                        {
                            // Attempt to set the new balance via the Economics plugin
                            bool success = SetEconomicsBalance(steamId, newBalance);
                            if (success)
                            {
                                Puts($"Synchronized in-game balance for player {player.Name} (SteamID: {steamId}) to {newBalance}.");

                                // Step 1: Send the updated balance to the website
                                SendBalanceToWebsite(player, newBalance, "set", () =>
                                {
                                    // Step 2: Retrieve the balance from the website to confirm synchronization
                                    SyncPlayerBalanceWithWebsite(steamId);
                                });
                            }
                            else
                            {
                                Puts($"Failed to set balance for {steamId} via Economics plugin.");
                                return "{ \"error\": \"Failed to set balance via Economics plugin.\" }";
                            }
                        }
                        else
                        {
                            Puts($"Balance for {steamId} is already up-to-date in-game.");
                        }
                    }
                    else
                    {
                        // Player is offline; balance is set in local storage only
                        Puts($"Player {steamId} is offline. Balance set to {newBalance} in database.");

                        // Even if the player is offline, send the balance to website and sync
                        SendBalanceToWebsite(null, newBalance, "set", () =>
                        {
                            SyncPlayerBalanceWithWebsite(steamId);
                        });
                    }

                    return "{ \"status\": \"Balance update initiated successfully.\" }";
                }
                else
                {
                    Puts($"Invalid balance amount received: {balanceData["balance"]}");
                    return "{ \"error\": \"Invalid balance amount.\" }";
                }
            }

            Puts("Balance update request missing 'balance'.");
            return "{ \"error\": \"Missing 'balance' in balance data.\" }";
        }

        private void HandleGetBalance(Dictionary<string, string> queryParams, Action<string> responseCallback)
        {
            // This method is now handled by HandleGetBalanceRequest
        }

        /// <summary>
        /// Handles the UserCheck HTTP request to check for missing users.
        /// </summary>
        /// <param name="responseCallback">The callback to send the response.</param>
        private void HandleUserCheck(Action<string> callback)
        {
            // Fetch all players' SteamIDs from the Rust server
            var allPlayers = covalence.Players.All;
            var steamIds = allPlayers.Select(p => p.Id).ToList();

            // Retrieve UserAccounts from the database
            GetUserAccountsFromDatabase(existingUserAccounts =>
            {
                // Find SteamIDs that are not in the existing UserAccounts
                var missingSteamIds = steamIds.Where(id => !existingUserAccounts.Contains(id)).ToList();

                if (missingSteamIds.Count == 0)
                {
                    Puts("No missing users found during user account check.");
                    callback("{ \"status\": \"No missing users to process.\" }");
                    return;
                }

                Puts($"Found {missingSteamIds.Count} missing user(s) during user account check.");

                int processedCount = 0;
                foreach (var steamId in missingSteamIds)
                {
                    var player = covalence.Players.FindPlayerById(steamId);
                    if (player != null)
                    {
                        CreateUserOnWebsite(player, () =>
                        {
                            // After successful creation, add to the database
                            AddUserAccountToDatabase(steamId, () =>
                            {
                                SaveConfig();
                                Puts($"Added missing user {player.Name} (SteamID: {steamId}) to Azuriom.");

                                // Optionally, add to Transaction Ledger
                                AddTransactionToLedger("registration", steamId, 0.0, "User account creation");

                                processedCount++;
                                if (processedCount == missingSteamIds.Count)
                                {
                                    callback("{ \"status\": \"User check processed.\" }");
                                }
                            });
                        });
                    }
                    else
                    {
                        Puts($"Player with SteamID: {steamId} not found during user account check.");
                        processedCount++;
                        if (processedCount == missingSteamIds.Count)
                        {
                            callback("{ \"status\": \"User check processed.\" }");
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Retrieves the list of UserAccounts from the database asynchronously.
        /// </summary>
        /// <param name="callback">Callback with the list of existing UserAccounts.</param>
        private void GetUserAccountsFromDatabase(Action<List<string>> callback)
        {
            var sql = Core.Database.Sql.Builder.Append("SELECT steam_id FROM user_accounts");
            Sqlite.Query(sql, Sqlite_conn, list =>
            {
                List<string> userAccounts = new List<string>();
                foreach (var record in list)
                {
                    if (record.ContainsKey("steam_id"))
                    {
                        userAccounts.Add(record["steam_id"].ToString());
                    }
                }
                callback(userAccounts);
            });
        }

        /// <summary>
        /// Adds a new UserAccount to the database asynchronously.
        /// </summary>
        /// <param name="steamId">The SteamID to add.</param>
        /// <param name="callback">Callback after the operation is complete.</param>
        private void AddUserAccountToDatabase(string steamId, Action callback)
        {
            var sql = Core.Database.Sql.Builder.Append(@"
                INSERT INTO user_accounts (steam_id) 
                VALUES (@0)
                ON CONFLICT(steam_id) DO NOTHING;",
                steamId);

            Sqlite.Insert(sql, Sqlite_conn, rowsAffected =>
            {
                Puts($"[AzSync] Added user account {steamId} to the database. Rows affected: {rowsAffected}");
                callback();
            });
        }

        #endregion

        #region Balance Management

        /// <summary>
        /// Updates the player's balance by adding, subtracting, or setting it to a specific value.
        /// </summary>
        /// <param name="steamId">The player's SteamID.</param>
        /// <param name="amount">The amount to add or subtract. Ignored if setting balance.</param>
        /// <param name="operation">The type of balance operation.</param>
        /// <returns>True if the operation was successful; otherwise, false.</returns>
        private void SetPlayerBalance(string steamId, double amount, BalanceOperation operation, Action<bool> callback)
        {
            lock (balanceLock)
            {
                GetDatabaseBalance(steamId, existingBalance =>
                {
                    double newBalance = existingBalance;
                    switch (operation)
                    {
                        case BalanceOperation.Add:
                            newBalance += amount;
                            break;

                        case BalanceOperation.Subtract:
                            newBalance -= amount;
                            break;

                        case BalanceOperation.Set:
                            newBalance = amount;
                            break;

                        default:
                            Puts($"Invalid balance operation: {operation}");
                            callback(false);
                            return;
                    }

                    // Prevent negative balances
                    if (newBalance < 0)
                    {
                        Puts($"Attempted to set negative balance for SteamID: {steamId}. Operation aborted.");
                        callback(false);
                        return;
                    }

                    // Update SQLite database
                    bool upsertSuccess = UpsertUserBalance(steamId, newBalance);
                    if (!upsertSuccess)
                    {
                        callback(false);
                        return;
                    }

                    Puts($"Updated balance for SteamID: {steamId} to {newBalance} in database.");

                    // Invoke callback to indicate success
                    callback(true);

                    // Log the transaction in the SQLite ledger
                    string transactionType = operation.ToString().ToLower();
                    string targetSteamId = "";

                    if (operation == BalanceOperation.Set)
                    {
                        targetSteamId = ""; // No target for set operations
                    }

                    AddTransactionToLedger(transactionType, steamId, operation == BalanceOperation.Set ? newBalance : amount, targetSteamId);

                    // Attempt to synchronize with Economics plugin if user is online
                    var player = covalence.Players.FindPlayerById(steamId);
                    if (player != null)
                    {
                        bool success = SetEconomicsBalance(steamId, newBalance);
                        if (success)
                        {
                            Puts($"Synchronized in-game balance for player {player.Name} (SteamID: {steamId}) to {newBalance}.");
                        }
                        else
                        {
                            Puts($"Failed to synchronize in-game balance for player {player.Name} (SteamID: {steamId}).");
                        }
                    }
                    else
                    {
                        Puts($"Player {steamId} is offline. Balance updated in database only.");
                    }
                });
            }
        }

        /// <summary>
        /// Retrieves the player's balance from the database.
        /// </summary>
        /// <param name="steamId">The player's SteamID.</param>
        /// <param name="callback">The callback to execute with the balance.</param>
        private void GetPlayerBalance(string steamId, Action<double> callback)
        {
            GetDatabaseBalance(steamId, callback);
        }

        #endregion

        #region Synchronization Methods

        private class BalanceData
        {
            [JsonProperty("game_id")]
            public string SteamId { get; set; }

            [JsonProperty("money")]
            public double Balance { get; set; }
        }

        /// <summary>
        /// Fetches balances from the Azuriom website and updates in-game balances accordingly.
        /// This method is called once during plugin initialization.
        /// </summary>
        private void FetchAndSyncBalancesFromWebsiteAsync()
        {
            if (isSyncing)
            {
                Puts("Balance synchronization is already in progress.");
                return;
            }

            isSyncing = true;
            Puts("Starting balance synchronization: Fetching balances from Azuriom website.");

            string fetchUrl = $"{pluginConfig.Azuriom.BaseUrl}azlink/users";

            Puts($"Fetching balances from URL: {fetchUrl}");

            var headers = GetRequestHeaders(); // Get headers including SiteKey
            webrequest.Enqueue(fetchUrl, null, (code, response) =>
            {
                if (code == 200)
                {
                    try
                    {
                        var balances = JsonConvert.DeserializeObject<List<BalanceData>>(response);
                        Puts($"Fetched {balances.Count} balance record(s) from Azuriom.");

                        foreach (var balance in balances)
                        {
                            if (string.IsNullOrEmpty(balance.SteamId))
                            {
                                Puts("Encountered a balance record with an empty SteamId. Skipping.");
                                continue;
                            }

                            // Retrieve the current balance from the database
                            GetDatabaseBalance(balance.SteamId, dbBalance =>
                            {
                                // Update SQLite if there's a discrepancy
                                if (Math.Abs(dbBalance - balance.Balance) > 0.01)
                                {
                                    // Update SQLite with the website balance
                                    bool upsertSuccess = UpsertUserBalance(balance.SteamId, balance.Balance);
                                    if (upsertSuccess)
                                    {
                                        Puts($"Updated balance for SteamID: {balance.SteamId} to {balance.Balance} in the database.");

                                        // Find the player in-game
                                        var player = covalence.Players.FindPlayerById(balance.SteamId);
                                        if (player != null)
                                        {
                                            bool setSuccess = SetEconomicsBalance(balance.SteamId, balance.Balance);
                                            if (setSuccess)
                                            {
                                                Puts($"Synchronized in-game balance for player {player.Name} (SteamID: {balance.SteamId}) to {balance.Balance}.");
                                                SendBalanceToWebsite(player, balance.Balance, "set");
                                            }
                                            else
                                            {
                                                Puts($"Failed to synchronize in-game balance for player {player.Name} (SteamID: {balance.SteamId}).");
                                            }
                                        }
                                        else
                                        {
                                            Puts($"Player with SteamID: {balance.SteamId} is offline. Balance updated in database only.");
                                        }
                                    }
                                }
                            });
                        }
                        Puts("Balance synchronization from Azuriom completed successfully.");
                    }
                    catch (JsonException ex)
                    {
                        Puts($"Error parsing balances from website: {ex.Message}");
                    }
                }
                else
                {
                    Puts($"Failed to fetch balances from website. Status Code: {code}, Response: {response}");
                }

                isSyncing = false;
            }, this, RequestMethod.GET, headers);
        }

        #endregion

        #region Event Hooks

        // Hook into player connection to initialize their data and ensure they're registered on the website
        public void OnUserConnected(IPlayer player)
        {
            string steamId = player.Id;

            // Check if user exists on the website
            FetchUserByGameId(steamId, user =>
            {
                if (user != null)
                {
                    // User exists on website, ensure they are in the database
                    UserExistsInDatabase(steamId, existsInDb =>
                    {
                        if (!existsInDb)
                        {
                            // Retrieve the balance from the website
                            double websiteBalance = GetWebsiteBalance(user);

                            // Add user to the database with their website balance
                            UpsertUserBalance(steamId, websiteBalance);
                            Puts($"Added user {steamId} with balance {websiteBalance} to the database.");
                        }

                        // Synchronize in-game balance with SQLite
                        GetDatabaseBalance(steamId, dbBalance =>
                        {
                            double currentEconomicsBalance = GetEconomicsBalance(steamId);

                            if (Math.Abs(dbBalance - currentEconomicsBalance) > 0.01)
                            {
                                bool success = SetEconomicsBalance(steamId, dbBalance);
                                if (success)
                                {
                                    Puts($"Synchronized in-game balance for player {player.Name} (SteamID: {steamId}) to {dbBalance}.");
                                    SendBalanceToWebsite(player, dbBalance, "set");
                                }
                                else
                                {
                                    Puts($"Failed to synchronize in-game balance for player {player.Name} (SteamID: {steamId}).");
                                }
                            }
                            else
                            {
                                Puts($"No synchronization needed for player {player.Name} (SteamID: {steamId}). Balances are already synchronized.");
                            }
                        });
                    });
                }
                else
                {
                    // User does not exist on website; create on website
                    CreateUserOnWebsite(player, () =>
                    {
                        // Initialize user in the database with a balance of 0.0
                        UpsertUserBalance(steamId, 0.0);
                        Puts($"User {player.Name} (SteamID: {steamId}) created on Azuriom and added to the database with a balance of 0.0.");

                        // Initialize in-game balance
                        bool success = SetEconomicsBalance(steamId, 0.0);
                        if (success)
                        {
                            Puts($"Initialized in-game balance for player {player.Name} (SteamID: {steamId}) to 0.0.");
                            SendBalanceToWebsite(player, 0.0, "set");
                        }
                        else
                        {
                            Puts($"Failed to initialize in-game balance for player {player.Name} (SteamID: {steamId}).");
                        }

                        // Record the user creation in the ledger
                        AddTransactionToLedger("registration", steamId, 0.0, "User account creation on connection");
                    });
                }
            });
        }

        /// <summary>
        /// Handles the deposit event from the Economics plugin by adding the specified amount to the user's balance.
        /// </summary>
        private void OnEconomicsDeposit(string playerId, double amount)
        {
            IPlayer player = covalence.Players.FindPlayerById(playerId);
            if (player != null)
            {
                // Check if user exists in the database
                UserExistsInDatabase(playerId, exists =>
                {
                    if (!exists)
                    {
                        Puts($"[AzSync] Deposit Failed: User {playerId} does not exist in the database.");
                        return;
                    }

                    // Retrieve current balance from the database
                    GetDatabaseBalance(playerId, currentBalance =>
                    {
                        double newBalance = currentBalance + amount;

                        // Update the balance in the SQLite database
                        bool success = UpsertUserBalance(playerId, newBalance);
                        if (success)
                        {
                            Puts($"[AzSync] Balance Updated: Added {amount} to user {player.Name} (SteamID: {playerId}). New balance: {newBalance}.");

                            // Update in-game balance via Economics plugin
                            bool economicsSuccess = SetEconomicsBalance(playerId, newBalance);
                            if (economicsSuccess)
                            {
                                Puts($"[AzSync] In-Game Balance Synchronized: Added {amount} to in-game balance for {player.Name} (SteamID: {playerId}).");

                                // Synchronize the updated balance with the Azuriom website
                                SendBalanceToWebsite(player, newBalance, "deposit", () =>
                                {
                                    Puts($"[AzSync] Successfully synchronized deposit to Azuriom website for {player.Name} (SteamID: {playerId}).");
                                });

                                // Record the deposit in the transaction ledger
                                AddTransactionToLedger("deposit", playerId, amount, "Balance added via Economics plugin");
                            }
                            else
                            {
                                Puts($"[AzSync] Deposit Failed: Unable to synchronize in-game balance via Economics plugin for {player.Name} (SteamID: {playerId}).");
                                // Optionally, implement a rollback mechanism here if needed
                            }
                        }
                        else
                        {
                            Puts($"[AzSync] Deposit Failed: Unable to update balance in the database for {player.Name} (SteamID: {playerId}).");
                            // Optionally, notify the player about the failure here
                        }
                    });
                });
            }
            else
            {
                Puts($"[AzSync] Deposit Failed: Player with SteamID: {playerId} not found during deposit event.");
            }
        }

        /// <summary>
        /// Handles the withdrawal event from the Economics plugin by subtracting the specified amount from the user's balance.
        /// </summary>
        private void OnEconomicsWithdrawl(string playerId, double amount)
        {
            IPlayer player = covalence.Players.FindPlayerById(playerId);
            if (player != null)
            {
                // Check if user exists in the database
                UserExistsInDatabase(playerId, exists =>
                {
                    if (!exists)
                    {
                        Puts($"[AzSync] Withdrawal Failed: User {playerId} does not exist in the database.");
                        return;
                    }

                    // Retrieve current balance from the database
                    GetDatabaseBalance(playerId, currentBalance =>
                    {
                        if (currentBalance < amount)
                        {
                            Puts($"[AzSync] Withdrawal Failed: User {player.Name} (SteamID: {playerId}) has insufficient balance. Current: {currentBalance}, Attempted to subtract: {amount}");
                            // Optionally, notify the player about the insufficient balance here
                            return;
                        }

                        double newBalance = currentBalance - amount;

                        // Update the balance in the SQLite database
                        bool success = UpsertUserBalance(playerId, newBalance);
                        if (success)
                        {
                            Puts($"[AzSync] Balance Updated: Subtracted {amount} from user {player.Name} (SteamID: {playerId}). New balance: {newBalance}.");

                            // Update in-game balance via Economics plugin
                            bool economicsSuccess = SetEconomicsBalance(playerId, newBalance);
                            if (economicsSuccess)
                            {
                                Puts($"[AzSync] In-Game Balance Synchronized: Subtracted {amount} from in-game balance for {player.Name} (SteamID: {playerId}).");

                                // Synchronize the updated balance with the Azuriom website
                                SendBalanceToWebsite(player, newBalance, "withdrawal", () =>
                                {
                                    Puts($"[AzSync] Successfully synchronized withdrawal to Azuriom website for {player.Name} (SteamID: {playerId}).");
                                });

                                // Record the withdrawal in the transaction ledger
                                AddTransactionToLedger("withdrawal", playerId, amount, "Balance subtracted via Economics plugin");
                            }
                            else
                            {
                                Puts($"[AzSync] Withdrawal Failed: Unable to synchronize in-game balance via Economics plugin for {player.Name} (SteamID: {playerId}).");
                                // Optionally, implement a rollback mechanism here if needed
                            }
                        }
                        else
                        {
                            Puts($"[AzSync] Withdrawal Failed: Unable to update balance in the database for {player.Name} (SteamID: {playerId}).");
                            // Optionally, notify the player about the failure here
                        }
                    });
                });
            }
            else
            {
                Puts($"[AzSync] Withdrawal Failed: Player with SteamID: {playerId} not found during withdrawal synchronization.");
            }
        }

        /// <summary>
        /// Handles the transfer event from the Economics plugin by moving the specified amount from the sender to the receiver.
        /// </summary>
        private void OnEconomicsTransfer(string playerId, string targetId, double amount)
        {
            // Fetch sender and receiver players
            var sender = covalence.Players.FindPlayerById(playerId);
            var receiver = covalence.Players.FindPlayerById(targetId);

            if (sender == null)
            {
                Puts($"Sender with ID {playerId} not found.");
                return;
            }

            if (receiver == null)
            {
                Puts($"Receiver with ID {targetId} not found.");
                return;
            }

            // Log the transfer
            Puts($"{sender.Name} (SteamID: {sender.Id}) is attempting to transfer {amount} to {receiver.Name} (SteamID: {receiver.Id}).");

            // Check if both users exist in the database
            UserExistsInDatabase(playerId, senderExists =>
            {
                if (!senderExists)
                {
                    Puts($"Sender {playerId} does not exist in the database.");
                    return;
                }

                UserExistsInDatabase(targetId, receiverExists =>
                {
                    if (!receiverExists)
                    {
                        Puts($"Receiver {targetId} does not exist in the database.");
                        return;
                    }

                    // Retrieve sender's current balance
                    GetDatabaseBalance(playerId, senderBalance =>
                    {
                        if (senderBalance < amount)
                        {
                            Puts($"Sender {playerId} has insufficient balance. Current: {senderBalance}, Attempted to transfer: {amount}");
                            // Optionally, notify the sender about the insufficient balance here
                            return;
                        }

                        double newSenderBalance = senderBalance - amount;

                        // Update sender's balance
                        bool senderUpdateSuccess = UpsertUserBalance(playerId, newSenderBalance);
                        if (!senderUpdateSuccess)
                        {
                            Puts($"Failed to update sender's balance for SteamID: {playerId}.");
                            return;
                        }

                        // Retrieve receiver's current balance
                        GetDatabaseBalance(targetId, receiverBalance =>
                        {
                            double newReceiverBalance = receiverBalance + amount;

                            // Update receiver's balance
                            bool receiverUpdateSuccess = UpsertUserBalance(targetId, newReceiverBalance);
                            if (!receiverUpdateSuccess)
                            {
                                Puts($"Failed to update receiver's balance for SteamID: {targetId}.");

                                // **Optionally, revert sender's balance if receiver update fails**
                                bool revertSuccess = UpsertUserBalance(playerId, senderBalance);
                                if (revertSuccess)
                                {
                                    Puts($"Reverted sender's balance for SteamID: {playerId} due to receiver update failure.");
                                }
                                else
                                {
                                    Puts($"Critical: Failed to revert sender's balance for SteamID: {playerId}.");
                                }
                                return;
                            }

                            // Update in-game balances if players are online
                            UpdateInGameBalances(sender, receiver, playerId, targetId, amount);

                            // Log the successful transfer
                            Puts($"Processed transfer of {amount} from {sender.Name} (SteamID: {playerId}) to {receiver.Name} (SteamID: {targetId}).");

                            // **Corrected Call: AddTransactionToLedger with 4 arguments**
                            AddTransactionToLedger("transfer", playerId, amount, targetId);
                        });
                    });
                });
            });
        }

        /// <summary>
        /// Updates the in-game balances for both sender and receiver.
        /// </summary>
        private void UpdateInGameBalances(IPlayer sender, IPlayer receiver, string playerId, string targetId, double amount)
        {
            // Update sender's in-game balance
            bool senderEconomicsSuccess = SetEconomicsBalance(playerId, GetEconomicsBalance(playerId) - amount);
            if (senderEconomicsSuccess)
            {
                Puts($"Subtracted {amount} from in-game balance for {sender.Name} (SteamID: {playerId}).");
                SendBalanceToWebsite(sender, amount, "withdrawal", () =>
                {
                    // Sync sender's balance from website
                    SyncPlayerBalanceWithWebsite(sender.Id);
                });
            }
            else
            {
                Puts($"Failed to subtract balance via Economics plugin for {sender.Name} (SteamID: {playerId}).");
            }

            // Update receiver's in-game balance
            bool receiverEconomicsSuccess = SetEconomicsBalance(targetId, GetEconomicsBalance(targetId) + amount);
            if (receiverEconomicsSuccess)
            {
                Puts($"Added {amount} to in-game balance for {receiver.Name} (SteamID: {targetId}).");
                SendBalanceToWebsite(receiver, amount, "deposit", () =>
                {
                    // Sync receiver's balance from website
                    SyncPlayerBalanceWithWebsite(receiver.Id);
                });
            }
            else
            {
                Puts($"Failed to add balance via Economics plugin for {receiver.Name} (SteamID: {targetId}).");
            }
        }

        /// <summary>
        /// Called when all economics data is wiped.
        /// </summary>
        public void OnEconomicsDataWiped()
        {
            Puts("OnEconomicsDataWiped called.");

            // Remove all user balances from the SQLite database
            RemoveAllUserBalances();

            // Remove all user accounts from the SQLite database
            RemoveAllUserAccounts();

            // Remove all transactions from the SQLite ledger
            RemoveAllTransactions();

            Puts("All economic data has been wiped. Cleared SQLite storage and ledger.");
        }

        /// <summary>
        /// Removes all user balances from the SQLite database.
        /// </summary>
        private void RemoveAllUserBalances()
        {
            var sql = Core.Database.Sql.Builder.Append("DELETE FROM user_balances;");
            Sqlite.Delete(sql, Sqlite_conn, rowsAffected =>
            {
                Puts($"All user balances have been cleared from the database. Rows affected: {rowsAffected}");
            });
        }

        /// <summary>
        /// Removes all user accounts from the SQLite database.
        /// </summary>
        private void RemoveAllUserAccounts()
        {
            try
            {
                var sql = Core.Database.Sql.Builder.Append("DELETE FROM user_accounts;");
                Sqlite.Delete(sql, Sqlite_conn, rowsAffected =>
                {
                    Puts($"All user accounts have been cleared from the database. Rows affected: {rowsAffected}");
                });
            }
            catch (Exception ex)
            {
                Puts($"[AzSync] Error clearing user accounts: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes all transactions from the SQLite ledger.
        /// </summary>
        private void RemoveAllTransactions()
        {
            try
            {
                var sql = Core.Database.Sql.Builder.Append("DELETE FROM transactions;");
                Sqlite.Delete(sql, Sqlite_conn, rowsAffected =>
                {
                    Puts($"All transactions have been cleared from the ledger. Rows affected: {rowsAffected}");
                });
            }
            catch (Exception ex)
            {
                Puts($"[AzSync] Error clearing transaction ledger: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when economics data for a specific player is wiped.
        /// </summary>
        public void OnEconomicsDataWiped(IPlayer player)
        {
            string steamId = player.Id;
            Puts($"OnEconomicsDataWiped called for player: {player.Name} (SteamID: {steamId})");

            // Remove user balances from the SQLite database
            RemoveUserBalance(steamId, success =>
            {
                if (success)
                {
                    Puts($"Removed balance for player {player.Name} (SteamID: {steamId}) from the database.");
                }
                else
                {
                    Puts($"Failed to remove balance for player {player.Name} (SteamID: {steamId}) from the database.");
                }
            });

            // Remove user accounts from the SQLite database
            RemoveUserAccount(steamId, success =>
            {
                if (success)
                {
                    Puts($"Removed user account for player {player.Name} (SteamID: {steamId}) from the database.");
                }
                else
                {
                    Puts($"Failed to remove user account for player {player.Name} (SteamID: {steamId}) from the database.");
                }
            });

            // Remove all transactions related to this player from the SQLite ledger
            RemoveTransactionsBySteamId(steamId, success =>
            {
                if (success)
                {
                    Puts($"Removed transactions for player {player.Name} (SteamID: {steamId}) from the ledger.");
                }
                else
                {
                    Puts($"Failed to remove transactions for player {player.Name} (SteamID: {steamId}) from the ledger.");
                }
            });

            Puts($"Economic data wiped for player {player.Name} (SteamID: {steamId}).");
        }

        /// <summary>
        /// Removes the user's balance from the SQLite database.
        /// </summary>
        private void RemoveUserBalance(string steamId, Action<bool> callback)
        {
            try
            {
                var sql = Core.Database.Sql.Builder.Append("DELETE FROM user_balances WHERE steam_id = @0;", steamId);
                Sqlite.Delete(sql, Sqlite_conn, rowsAffected =>
                {
                    Puts($"[AzSync] Removed user balance for SteamID: {steamId}. Rows affected: {rowsAffected}");
                    callback(rowsAffected > 0);
                });
            }
            catch (Exception ex)
            {
                Puts($"[AzSync] Exception in RemoveUserBalance for SteamID: {steamId} - {ex.Message}");
                callback(false);
            }
        }

        /// <summary>
        /// Removes the user's account from the SQLite database.
        /// </summary>
        private void RemoveUserAccount(string steamId, Action<bool> callback)
        {
            var sql = Core.Database.Sql.Builder.Append("DELETE FROM user_accounts WHERE steam_id = @0;", steamId);
            Sqlite.Delete(sql, Sqlite_conn, rowsAffected =>
            {
                Puts($"[AzSync] Removed user account for SteamID: {steamId}. Rows affected: {rowsAffected}");
                callback(rowsAffected > 0);
            });
        }

        /// <summary>
        /// Removes all transactions related to the specified SteamID from the SQLite ledger.
        /// </summary>
        private void RemoveTransactionsBySteamId(string steamId, Action<bool> callback)
        {
            var sql = Core.Database.Sql.Builder.Append("DELETE FROM transactions WHERE steam_id = @0;", steamId);
            Sqlite.Delete(sql, Sqlite_conn, rowsAffected =>
            {
                Puts($"[AzSync] Removed transactions for SteamID: {steamId}. Rows affected: {rowsAffected}");
                callback(rowsAffected > 0);
            });
        }

        #endregion

        #region Azuriom API Integration

        /// <summary>
        /// Sends balance changes to the Azuriom API for synchronization.
        /// </summary>
        /// <param name="player">The player whose balance is being synchronized.</param>
        /// <param name="amount">The amount of money involved in the transaction.</param>
        /// <param name="action">The action type ("deposit", "withdrawal", "set").</param>
        /// <param name="onSuccess">Callback to execute after successful synchronization.</param>
        private void SendBalanceToWebsite(IPlayer player, double amount, string action, Action onSuccess = null)
        {
            string steamId = player != null ? player.Id : "unknown";
            string playerName = player != null ? player.Name : "unknown";

            Puts($"Preparing to send balance to website for player {playerName} (SteamID: {steamId}), Amount: {amount}, Action: {action}");

            FetchUserByGameId(steamId, user =>
            {
                if (user != null)
                {
                    if (user.TryGetValue("id", out var userIdObj))
                    {
                        string userId = userIdObj.ToString();
                        Puts($"Extracted Azuriom User ID: {userId} for player {playerName} (SteamID: {steamId})");

                        string baseUrl = pluginConfig.Azuriom.BaseUrl.TrimEnd('/');
                        string syncUrl = string.Empty;

                        switch (action.ToLower())
                        {
                            case "deposit":
                                syncUrl = $"{baseUrl}/azlink/user/{userId}/money/add";
                                break;
                            case "withdrawal":
                                syncUrl = $"{baseUrl}/azlink/user/{userId}/money/remove";
                                break;
                            case "set":
                                syncUrl = $"{baseUrl}/azlink/user/{userId}/money/set";
                                break;
                            default:
                                Puts($"Invalid action type: {action}");
                                return;
                        }

                        var payload = JsonConvert.SerializeObject(new { amount = amount });

                        var headers = GetRequestHeaders(payload);

                        Puts($"Sending {action} request to {syncUrl} with payload: {payload}");

                        webrequest.Enqueue(syncUrl, payload, (code, response) =>
                        {
                            if (code == 200 || code == 204)
                            {
                                Puts($"Successfully synchronized balance for player {playerName} with Azuriom. Action: {action}, Amount: {amount}");
                                onSuccess?.Invoke(); // Invoke the callback after successful set
                            }
                            else
                            {
                                Puts($"Failed to synchronize balance for player {playerName}. Response code: {code}");
                                Puts($"Response: {response}");
                            }
                        }, this, RequestMethod.POST, headers);
                    }
                    else
                    {
                        Puts($"User data for player {playerName} does not contain 'id'. Cannot synchronize balance.");
                    }
                }
                else
                {
                    Puts($"User data not found for player {playerName} (SteamID: {steamId}). Cannot synchronize balance.");
                }
            });
        }

        /// <summary>
        /// Fetches user data from Azuriom based on SteamID.
        /// </summary>
        public void FetchUserByGameId(string steamId, Action<Dictionary<string, object>> callback)
        {
            var fetchUrl = $"{pluginConfig.Azuriom.BaseUrl}azlink/user-by-game-id/{steamId}";

            var headers = GetRequestHeaders(); // No payload for GET request

            Puts($"Fetching user data from Azuriom for SteamID: {steamId}");

            webrequest.Enqueue(fetchUrl, null, (code, response) =>
            {
                if (code == 200)
                {
                    try
                    {
                        var user = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);
                        Puts($"Successfully fetched user data for SteamID: {steamId}");
                        callback(user);
                    }
                    catch (JsonException ex)
                    {
                        Puts($"Error parsing JSON response for SteamID: {steamId}: {ex.Message}");
                        Puts($"Response: {response}");
                        callback(null);
                    }
                }
                else
                {
                    Puts($"Failed to fetch user by SteamID: {steamId}. Response code: {code}");
                    Puts($"Response: {response}");
                    callback(null);
                }
            }, this, RequestMethod.GET, headers);
        }

        private void CreateUserOnWebsite(IPlayer player, Action onSuccess)
        {
            var registerUrl = $"{pluginConfig.Azuriom.BaseUrl}azlink/register";
            var password = Guid.NewGuid().ToString("N").Substring(0, 12); // Generate a secure random password

            // Sanitize player name
            var playerName = Regex.Replace(player.Name, @"[^\u0000-\u007F]+", string.Empty); // Remove non-ASCII characters
            playerName = playerName.Length > 25 ? playerName.Substring(0, 25) : playerName;

            var payload = JsonConvert.SerializeObject(new
            {
                name = playerName,
                steam_id = player.Id,
                role = "Member",
                money = 0, // Initial balance
                password = password
            });

            var headers = GetRequestHeaders(payload);

            Puts($"Creating user on Azuriom for player {player.Name} (SteamID: {player.Id}) with payload: {payload}");

            webrequest.Enqueue(registerUrl, payload, (code, response) =>
            {
                if (code == 200 || code == 201 || code == 204)
                {
                    Puts($"Created account for player {player.Name} (SteamID: {player.Id}).");
                    onSuccess?.Invoke();
                }
                else
                {
                    Puts($"Failed to create user on website for player {player.Name}. Response code: {code}");
                    Puts($"Response: {response}");
                }
            }, this, RequestMethod.POST, headers);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Retrieves the necessary headers for HTTP requests.
        /// </summary>
        /// <param name="payload">Optional JSON payload for POST/PUT requests.</param>
        /// <returns>A dictionary containing the required headers.</returns>
        private Dictionary<string, string> GetRequestHeaders(string payload = null)
        {
            var headers = new Dictionary<string, string>
            {
                { "Azuriom-Link-Token", pluginConfig.Azuriom.SiteKey }
            };

            if (!string.IsNullOrEmpty(payload))
            {
                headers.Add("Content-Type", "application/json");
                // Content-Length is handled automatically by webrequest.Enqueue
            }

            return headers;
        }

        /// <summary>
        /// Retrieves the balance from the website's user data.
        /// </summary>
        /// <param name="user">The user data dictionary.</param>
        /// <returns>The balance as a double.</returns>
        private double GetWebsiteBalance(Dictionary<string, object> user)
        {
            if (user != null && user.TryGetValue("money", out var moneyObj))
            {
                if (moneyObj != null && double.TryParse(moneyObj.ToString(), out double websiteBalance))
                {
                    return websiteBalance;
                }
                else
                {
                    Puts($"Unable to parse 'money' value from user data: {moneyObj}");
                }
            }
            else
            {
                Puts($"'money' key not found in user data: {JsonConvert.SerializeObject(user)}");
            }
            return 0.0;
        }

        /// <summary>
        /// Retrieves the balance for a given player from the Economics plugin.
        /// </summary>
        /// <param name="gameId">The player's GameID (SteamID).</param>
        /// <returns>The balance as a double.</returns>
        private double GetEconomicsBalance(string gameId)
        {
            try
            {
                object result = EconomicsPlugin.Call("Balance", gameId);
                if (result is double balance)
                {
                    return balance;
                }
                else
                {
                    Puts($"Economics plugin returned non-double balance for {gameId}: {result}");
                    return 0.0;
                }
            }
            catch (Exception ex)
            {
                Puts($"Exception when getting balance for {gameId}: {ex.Message}");
                return 0.0;
            }
        }

        /// <summary>
        /// A method to retrieve all player balances from the SQLite database.
        /// </summary>
        /// <returns>A dictionary containing SteamID keys and balance values.</returns>
        private void GetAllPlayerBalances(Action<Dictionary<string, double>> callback)
        {
            var sql = Core.Database.Sql.Builder.Append("SELECT steam_id, balance FROM user_balances");

            Sqlite.Query(sql, Sqlite_conn, list =>
            {
                var playerBalances = new Dictionary<string, double>();

                if (list != null && list.Count > 0)
                {
                    foreach (var record in list)
                    {
                        if (record.ContainsKey("steam_id") && record.ContainsKey("balance") &&
                            double.TryParse(record["balance"].ToString(), out double balance))
                        {
                            playerBalances[record["steam_id"].ToString()] = balance;
                        }
                    }
                }

                callback(playerBalances);
            });
        }

        /// <summary>
        /// Sets the balance for a given player using the Economics plugin.
        /// </summary>
        /// <param name="gameId">The player's GameID (SteamID).</param>
        /// <param name="amount">The new balance amount.</param>
        /// <returns>True if successful; otherwise, false.</returns>
        private bool SetEconomicsBalance(string gameId, double amount)
        {
            try
            {
                object result = EconomicsPlugin.Call("SetBalance", gameId, amount);
                if (result != null && (bool)result)
                {
                    return true;
                }
                else
                {
                    Puts($"Economics plugin returned failure when setting balance for {gameId}.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Puts($"Exception when setting balance for {gameId}: {ex.Message}");
                return false;
            }
        }

        private void SyncPlayerBalanceWithWebsite(string playerId)
        {
            FetchUserByGameId(playerId, user =>
            {
                if (user != null)
                {
                    double websiteBalance = GetWebsiteBalance(user);

                    // Use GetDatabaseBalance with a callback
                    GetDatabaseBalance(playerId, currentBalance =>
                    {
                        // Check if the balances are different beyond a small threshold
                        if (Math.Abs(currentBalance - websiteBalance) > 0.01)
                        {
                            // Attempt to update the in-game balance
                            bool success = SetEconomicsBalance(playerId, websiteBalance);

                            // Fetch the player object to determine online status
                            var player = covalence.Players.FindPlayerById(playerId);
                            if (player != null)
                            {
                                if (success)
                                {
                                    Puts($"Synchronized balance for player {player.Name} (ID: {playerId}). New balance: {websiteBalance}");
                                }
                                else
                                {
                                    Puts($"Failed to set balance for player {player.Name} (ID: {playerId}).");
                                }
                            }
                            else
                            {
                                if (success)
                                {
                                    Puts($"Synchronized balance for offline player (ID: {playerId}). New balance: {websiteBalance}");
                                }
                                else
                                {
                                    Puts($"Failed to set balance for offline player (ID: {playerId}).");
                                }
                            }
                        }
                        else
                        {
                            // Balances are effectively the same; no action needed
                            Puts($"No balance update required for player ID: {playerId}. Balances are already synchronized.");
                        }
                    });
                }
                else
                {
                    LogWarning($"User data not found for player ID: {playerId} during balance sync.");
                }
            });
        }

        #endregion

        #region Transaction Ledger Integration

        /// <summary>
        /// Adds a transaction record to the SQLite ledger.
        /// </summary>
        /// <param name="transactionType">Type of transaction ("deposit", "withdrawal", "transfer", etc.).</param>
        /// <param name="steamId">Player initiating the transaction.</param>
        /// <param name="amount">Amount involved in the transaction.</param>
        /// <param name="targetSteamId">Target player SteamID for transfers or additional details.</param>
        private void AddTransactionToLedger(string transactionType, string steamId, double amount, string targetSteamId = "")
        {
            var transaction = new TransactionRecord
            {
                Timestamp = DateTime.UtcNow.ToString("o"), // ISO 8601 format
                TransactionType = transactionType,
                SteamId = steamId,
                TargetSteamId = targetSteamId,
                Amount = amount
            };

            // Insert into SQLite database
            InsertTransaction(transaction);

            Puts($"Added transaction to ledger: {transactionType} of {amount} by {steamId}" +
                 (string.IsNullOrEmpty(targetSteamId) ? "" : $" to {targetSteamId}"));
        }

        #endregion

        #region Consistency Checks

        /// <summary>
        /// Verifies the consistency between SQLite and Economics plugin balances.
        /// </summary>
        /// <remarks>
        /// This method is called during plugin initialization to ensure that all balances are consistent.
        /// </remarks>
        private void VerifyConsistency()
        {
            Puts("Starting consistency verification between SQLite and Economics plugin balances.");

            GetAllPlayerSteamIds(steamIds =>
            {
                int totalPlayers = steamIds.Count;
                int processedPlayers = 0;

                foreach (var steamId in steamIds)
                {
                    GetDatabaseBalance(steamId, dbBalance =>
                    {
                        double economicsBalance = GetEconomicsBalance(steamId);

                        bool economicsConsistent = Math.Abs(dbBalance - economicsBalance) < 0.01;

                        if (!economicsConsistent && covalence.Players.FindPlayerById(steamId) != null)
                        {
                            Puts($"Discrepancy found for SteamID: {steamId}. Database: {dbBalance}, Economics: {economicsBalance}. Correcting Economics plugin balance.");
                            bool success = SetEconomicsBalance(steamId, dbBalance);
                            if (success)
                            {
                                Puts($"Corrected Economics plugin balance for SteamID: {steamId} to {dbBalance}.");
                                var player = covalence.Players.FindPlayerById(steamId);
                                if (player != null)
                                {
                                    SendBalanceToWebsite(player, dbBalance, "set");
                                }
                            }
                            else
                            {
                                Puts($"Failed to correct Economics plugin balance for SteamID: {steamId}.");
                            }
                        }

                        // Increment the processed players counter
                        processedPlayers++;
                        if (processedPlayers == totalPlayers)
                        {
                            Puts("Consistency verification completed.");
                        }
                    });
                }
            });
        }

        #endregion
    }
}
