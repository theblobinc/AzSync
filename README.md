# AzSync

**AzSync** is a Rust server plugin designed to asynchronously synchronize economic and user data between your Rust server and an Azuriom website. This integration ensures that player balances and transaction records remain consistent across both platforms, providing a seamless experience for your community.

## Features

- **Asynchronous Data Synchronization**: Ensures real-time synchronization of economic and user data between Rust and Azuriom.
- **HTTP Listener**: Handles incoming HTTP requests from the Azuriom CMS for balance updates and user management.
- **SQLite Integration**: Utilizes SQLite for storing transaction records, user balances, and account information.
- **Economics Plugin Integration**: Seamlessly works with the MIT Licensed Economics plugin to manage in-game currency.
- **Transaction Ledger**: Maintains a comprehensive ledger of all transactions for auditing and tracking purposes.
- **Consistency Verification**: Periodically checks and ensures consistency between in-game balances and website data.
- **User Management**: Automatically registers users on the Azuriom website upon connecting to the server.

## Requirements

- **Rust Server**: Running with Carbon or Oxide mod support.
- **Oxide Plugins**:
  - [Economics](https://umod.org/plugins/economics) plugin installed.
- **Azuriom Website**: Properly set up and configured to interact with the plugin.
- **.NET Framework**: Ensure that your server environment supports .NET for running C# plugins.

## Installation

1. **Download AzSync Plugin**:
   - Clone the repository or download the AzSync.cs file directly.
   
   ```bash
   git clone https://github.com/theblobinc/AzSync.git
   ```

2. **Install Dependencies**:
   - Ensure that the [Economics](https://umod.org/plugins/economics) plugin is installed on your Rust server.

3. **Add Plugin to Server**:
   - Place the AzSync.cs file into the `oxide/plugins` directory of your Rust server.

4. **Restart Server**:
   - Restart your Rust server or  reload the AzSync plugin.

## Configuration

AzSync uses a configuration file to manage settings related to the Azuriom API and synchronization parameters. Below is a sample configuration:

```json
{
  "Azuriom Settings": {
    "API Base URL": "https://yourazuriomsite.com/api/",
    "Site Key (ApiToken)": "your_site_key_here",
    "API IP Address": "127.0.0.1",
    "API Port": 8181
  }
}
```

### Configuration Options

- **API Base URL**: The base URL of your Azuriom website's API (e.g., `https://yourazuriomsite.com/api/`).
- **Site Key (ApiToken)**: Your Azuriom site's API token for authenticating requests.
- **API IP Address**: The IP address where the Azuriom API is hosted. Defaults to `127.0.0.1`.
- **API Port**: The port on which the Azuriom API listens. Defaults to `8181`.

**Note**: After placing the plugin on your server, the default configuration file will be generated. Make sure to update it with your specific settings.

## Usage

Once installed and configured, AzSync will automatically handle the synchronization process. Here are some key functionalities:

- **Automatic User Registration**: When a player connects to the server, AzSync checks if they exist on the Azuriom website. If not, it automatically creates their account.
- **Balance Synchronization**: Player balances are synchronized between the Rust server and Azuriom. Deposits, withdrawals, and transfers are reflected on both platforms.
- **Transaction Logging**: All transactions are logged in an SQLite database for record-keeping and auditing.
- **Consistency Checks**: AzSync runs periodic checks to ensure that balances remain consistent between the server and the website.

### HTTP Endpoints

AzSync sets up several HTTP endpoints to handle requests from the Azuriom CMS. Below is the detailed documentation for each endpoint:

---

#### **POST /azsync/set-balance**

**Description:**  
Sets a player's balance to a specific value.

**Request Headers:**
- 

Azuriom-Link-Token

 (string, required): API token for authentication.

**Request Body:**
```json
{
  "game_id": "string",    // Player's SteamID
  "balance": number       // New balance amount
}
```

**Response:**
- **Success (200 OK):**
  ```json
  {
    "status": "Balance update initiated successfully."
  }
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Missing or empty 'game_id' in balance data."
  }
  ```
- **Error (401 Unauthorized):**
  ```json
  {
    "error": "Unauthorized access"
  }
  ```
- **Error (500 Internal Server Error):**
  ```json
  {
    "error": "Internal server error"
  }
  ```

---

#### **GET /azsync/get-balance**

**Description:**  
Retrieves a player's current balance.

**Request Headers:**
- 

Azuriom-Link-Token

 (string, required): API token for authentication.

**Query Parameters:**
- `game_id` (string, required): Player's SteamID.

**Example Request:**
```
GET /azsync/get-balance?game_id=76561198000000000
```

**Response:**
- **Success (200 OK):**
  ```json
  {
    "steam_id": "76561198000000000",
    "balance": 1500.75
  }
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Missing or empty 'game_id' in query parameters."
  }
  ```
- **Error (401 Unauthorized):**
  ```json
  {
    "error": "Unauthorized access"
  }
  ```
- **Error (404 Not Found):**
  ```json
  {
    "error": "User not found"
  }
  ```
- **Error (500 Internal Server Error):**
  ```json
  {
    "error": "Internal server error"
  }
  ```

---

#### **GET /azsync/user-check**

**Description:**  
Checks for missing users and registers them on the Azuriom website.

**Request Headers:**
- 

Azuriom-Link-Token

 (string, required): API token for authentication.

**Response:**
- **Success (200 OK):**
  ```json
  {
    "status": "User check processed."
  }
  ```
- **Error (401 Unauthorized):**
  ```json
  {
    "error": "Unauthorized access"
  }
  ```
- **Error (500 Internal Server Error):**
  ```json
  {
    "error": "Internal server error"
  }
  ```

---

#### **POST /azsync/reset-balance**

**Description:**  
Resets a player's balance to zero.

**Request Headers:**
- 

Azuriom-Link-Token

 (string, required): API token for authentication.

**Request Body:**
```json
{
  "game_id": "string"    // Player's SteamID
}
```

**Response:**
- **Success (200 OK):**
  ```json
  {
    "status": "Balance reset successfully."
  }
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Missing or empty 'game_id' in request body."
  }
  ```
- **Error (401 Unauthorized):**
  ```json
  {
    "error": "Unauthorized access"
  }
  ```
- **Error (500 Internal Server Error):**
  ```json
  {
    "error": "Internal server error"
  }
  ```

---

#### **GET /azsync/get-transactions**

**Description:**  
Retrieves transaction records with optional filters.

**Request Headers:**
- 

Azuriom-Link-Token

 (string, required): API token for authentication.

**Query Parameters:**
- `start_time` (string, optional): Start of the time range in ISO 8601 format.
- `end_time` (string, optional): End of the time range in ISO 8601 format.
- 

limit

 (integer, optional): Number of records to retrieve (default: 100, max: 1000).
- 

offset

 (integer, optional): Number of records to skip (default: 0).

**Example Request:**
```
GET /azsync/get-transactions?start_time=2023-01-01T00:00:00Z&end_time=2023-12-31T23:59:59Z&limit=50&offset=100
```

**Response:**
- **Success (200 OK):**
  ```json
  [
    {
      "timestamp": "2023-04-25T14:30:00Z",
      "transaction_type": "deposit",
      "steam_id": "76561198000000000",
      "target_steam_id": "",
      "amount": 500.0
    },
    // More transactions...
  ]
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Invalid 'start_time' format. Use ISO 8601 format."
  }
  ```
- **Error (401 Unauthorized):**
  ```json
  {
    "error": "Unauthorized access"
  }
  ```
- **Error (500 Internal Server Error):**
  ```json
  {
    "error": "Internal server error"
  }
  ```

---

#### **POST /azsync/add-user**

**Description:**  
Adds a new user to the system.

**Request Headers:**
- 

Azuriom-Link-Token

 (string, required): API token for authentication.

**Request Body:**
```json
{
  "steam_id": "string",   // Player's SteamID
  "balance": number       // (Optional) Initial balance
}
```

**Response:**
- **Success (200 OK):**
  ```json
  {
    "status": "User added successfully."
  }
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Missing 'steam_id' in request body."
  }
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Invalid 'balance' value. Must be a number."
  }
  ```
- **Error (409 Conflict):**
  ```json
  {
    "error": "User already exists."
  }
  ```
- **Error (401 Unauthorized):**
  ```json
  {
    "error": "Unauthorized access"
  }
  ```
- **Error (500 Internal Server Error):**
  ```json
  {
    "error": "Internal server error"
  }
  ```

---

#### **POST /azsync/add-balance**

**Description:**  
Adds a specified amount to a user's account balance.

**Request Headers:**
- 

Azuriom-Link-Token

 (string, required): API token for authentication.

**Request Body:**
```json
{
  "steam_id": "string",    // Player's SteamID
  "amount": number         // Amount to add
}
```

**Response:**
- **Success (200 OK):**
  ```json
  {
    "status": "Balance added successfully."
  }
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Missing 'steam_id' or 'amount' in request body."
  }
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Invalid 'amount' value. Must be a positive number."
  }
  ```
- **Error (404 Not Found):**
  ```json
  {
    "error": "User does not exist."
  }
  ```
- **Error (401 Unauthorized):**
  ```json
  {
    "error": "Unauthorized access"
  }
  ```
- **Error (500 Internal Server Error):**
  ```json
  {
    "error": "Internal server error"
  }
  ```

---

#### **POST /azsync/subtract-balance**

**Description:**  
Subtracts a specified amount from a user's account balance.

**Request Headers:**
- 

Azuriom-Link-Token

 (string, required): API token for authentication.

**Request Body:**
```json
{
  "steam_id": "string",    // Player's SteamID
  "amount": number         // Amount to subtract
}
```

**Response:**
- **Success (200 OK):**
  ```json
  {
    "status": "Balance subtracted successfully."
  }
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Missing 'steam_id' or 'amount' in request body."
  }
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Invalid 'amount' value. Must be a positive number."
  }
  ```
- **Error (404 Not Found):**
  ```json
  {
    "error": "User does not exist."
  }
  ```
- **Error (400 Bad Request):**
  ```json
  {
    "error": "Insufficient balance."
  }
  ```
- **Error (401 Unauthorized):**
  ```json
  {
    "error": "Unauthorized access"
  }
  ```
- **Error (500 Internal Server Error):**
  ```json
  {
    "error": "Internal server error"
  }
  ```

---

#### **GET /azsync/get-all-balances**

**Description:**  
Retrieves the balances of all users.

**Request Headers:**
- 

Azuriom-Link-Token

 (string, required): API token for authentication.

**Response:**
- **Success (200 OK):**
  ```json
  {
    "balances": {
      "76561198000000000": 1500.75,
      "76561198000000001": 2500.00,
      // More users...
    }
  }
  ```
- **Error (401 Unauthorized):**
  ```json
  {
    "error": "Unauthorized access"
  }
  ```
- **Error (500 Internal Server Error):**
  ```json
  {
    "error": "Internal server error"
  }
  ```

---

**Note:**  
Ensure that your Azuriom website is configured to communicate with these endpoints using the correct API token (

Azuriom-Link-Token

). All requests must include this token in the headers for authentication purposes.

## Contribution

Contributions are welcome! Please follow these steps to contribute:

1. **Fork the Repository**: Click on the "Fork" button at the top-right corner of the repository page.
2. **Create a Feature Branch**:
   
   ```bash
   git checkout -b feature/YourFeatureName
   ```

3. **Commit Your Changes**:
   
   ```bash
   git commit -m "Add Your Feature"
   ```

4. **Push to the Branch**:
   
   ```bash
   git push origin feature/YourFeatureName
   ```

5. **Open a Pull Request**: Navigate to the original repository and create a pull request with a description of your changes.

## License

This project is licensed under the MIT License.

---

*Developed by [theblobinc](https://github.com/theblobinc)*
