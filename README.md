# AzSync

**AzSync** is a Rust server plugin designed to asynchronously synchronize economic and user data between your Rust server and an Azuriom website. This integration ensures that player balances and transaction records remain consistent across both platforms, providing a seamless experience for your community.

## Features

- **Asynchronous Data Synchronization**: Ensures real-time synchronization of economic and user data between Rust and Azuriom.
- **HTTP Listener**: Handles incoming HTTP requests from the Azuriom API for balance updates and user management.
- **SQLite Integration**: Utilizes SQLite for storing transaction records, user balances, and account information.
- **Economics Plugin Integration**: Seamlessly works with the Economics plugin to manage in-game currency.
- **Transaction Ledger**: Maintains a comprehensive ledger of all transactions for auditing and tracking purposes.
- **Consistency Verification**: Periodically checks and ensures consistency between in-game balances and website data.
- **User Management**: Automatically registers users on the Azuriom website upon connecting to the server.

## Requirements

- **Rust Server**: Running with Oxide mod support.
- **Oxide Plugins**:
  - [Economics](https://umod.org/plugins/economics) plugin installed.
- **Azuriom Website**: Properly set up and configured to interact with the plugin.
- **.NET Framework**: Ensure that your server environment supports .NET for running C# plugins.

## Installation

1. **Download AzSync Plugin**:
   - Clone the repository or download the 

AzSync.cs

 file directly.
   
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

AzSync sets up several HTTP endpoints to handle requests from the Azuriom API:

- 

POST /azsync/set-balance

: Sets a player's balance.
- 

GET /azsync/get-balance

: Retrieves a player's balance.
- 

GET /azsync/user-check

: Checks for missing users and registers them.
- 

POST /azsync/reset-balance

: Resets a player's balance to zero.
- 

GET /azsync/get-transactions

: Retrieves transaction records.
- 

POST /azsync/add-user

: Adds a new user.
- 

POST /azsync/add-balance

: Adds balance to a user's account.
- 

POST /azsync/subtract-balance

: Subtracts balance from a user's account.
- 

GET /azsync/get-all-balances

: Retrieves all user balances.

Ensure that your Azuriom website is configured to communicate with these endpoints using the correct API token.

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
