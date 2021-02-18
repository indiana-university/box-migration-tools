# Box Migration Automation Webhooks

These webhooks (HTTP API endpoints) faciliate the transition of an enterprise Box user to a read-only state. 

# Notice of Use

This code is provided as-is. This code has has been used in a production environment and to the best of our knowledge is free of significant bugs and defects. Use of this code is at your own risk. Indiana University assumes no liability for its use. Please use the GitHub Issues feature for questions or other problems. The maintainers of this repository will answer questions as they are able.

# Acknowledgements

This code was developed by the following [UITS](https://uits.iu.edu) staff at [Indiana Univerisity](https://iu.edu):

* [Venkata Bhupathi](https://github.com/vbhupathi)  
* [Jared Drake](https://github.com/jardrake)  
* [Jason Francis](https://github.com/jasonfrancis)  
* [Satish Garneni](https://github.com/sgarneni)  
* [Satwik Narlanka](https://github.com/satwiknarlanka)  
* [John Hoerr](https://github.com/jhoerr), Technical Lead  
* Nancy May, Product Owner
* Jacob Famer, Director

# Implementation

This solution is architected as a serverless HTTP API for the Azure Functions platform and written in C#. It makes heavy use of the [Box SDK](https://developer.box.com/sdks-and-tools/), which is available in several other languages (Java, Node, Python) and also has a CLI implementation. We will attempt to document the overall process in sufficient detail that it can be reimplememented in the architecture and language of your choosing.

# Local Testing

To build and run this Azure Functions project, first install the [.Net Core 3.1 SDK](https://dotnet.microsoft.com/download/dotnet-core/3.1). Then, from the command line, run:

```
dotnet build
```

If you'd like to run the functions locally, install the **v3** version of [Azure Functions Core Tools](https://www.npmjs.com/package/azure-functions-core-tools). Then, from the command line, run:

```
func start
```

A local web server will start and host the functions on `http://localhost:7071`. The API can then be engaged through a tool like curl or Postman. Note that a `local.settings.json` is required as described in the [Box Authentication](#box-authentication) and [Settings](#settings) section below.

# Box Authentication

These endpoints interact with the Box API. Before using these endpoints you must first [create a Box application](https://developer.box.com/guides/applications/) that uses [JWT Auth](https://developer.box.com/guides/authentication/jwt/). This application must have the following configuration settings:

**Authentication Method**: Must be *OAuth 2.0 with JWT (Server Authenticaiton)*  

**Application Access**: Must be *Enterprise*.  

**Application Scopes**: The following must be selected:     
  + Read and write all files and folders stored in box
  + Manage users
  + Manage groups

**Advanced Features**: The following must be selected:  
  + Perform Actions as Users
  + Generate User Access Tokens

Once the app is configured as described above, you'll want to create a public/private key pair. This will yield a `.json` file. The contents of this file (or a path to it) are required in order to create an authenticated Box client.

# Settings

## Required Settings

The following environment variables are *must be set* prior to using this code:

`BoxConfigJson`: The contents of the `.json` file described in the [Box Authentication](#box-authentication) section of this document.

## Optional Settings

The following environment variables are *can optionally be set* prior to using this code:

`LogFilePath`: A file system path at which a local log file should be created, to be used for long-term event logging.  

`LogTableStorageConnectionString`: A connection string to an [Azure Storage Table](https://azure.microsoft.com/en-us/services/storage/tables/), to be used for long-term event logging.  

`APPINSIGHTS_INSTRUMENTATIONKEY`: A GUID for an [Azure Application Insights](https://docs.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview) instance. **Important**: see [logging notes on Azure Application Insights](#azure-application-insights) prior to using this setting!

If you choose to host these endpoints on the Azure Functions platform, this repo includes a `local.settings.json.example` file. All required settings are included there. You can make a copy as `local.settings.json` and fill out required settings if you wish to run and debug the endpoints locally. This `local.settings.json` file can be pushed to Azure Functions when deploying the app using the [Azure Functions Core Tools](https://github.com/Azure/azure-functions-core-tools) CLI.

# Logging

Three logging sinks are currently supported.

## Local Log File

Logs events to the local file system. Requires setting the `LogFilePath` environment variable.

## Azure Table Storage

Logs to Azure Table Storage, a durable, low-cost, cloud-hosted table storage. You must first create an [Azure Storage](https://azure.microsoft.com/en-us/services/storage/) instance and fetch the connection string. Requires setting the `LogTableStorageConnectionString` environment variable.

## Azure Application Insights

Logs to Azure Application Insights, and ephemeral, high*-cost, cloud-hosted telemetry and monitoring service. Use App Insights only if you want to be alerted when endpoint errors occur. And **take care** to limit the sampling rate, data cap, and data retention period. These endpoints generate a lot of logs, and App Insights can quickly become expensive.

# Flow

1. Call `Bootstrap` once per user. This webhook will create a collaboration on a managed folder that will facilitate the change in ownership of a user's items.
2. Call `MoveItem` for each top-level item in a user's Box account. This webhook will move the top-level item into the managed folder.
3. Call `UpdateCollaborations` for each collaborated item in a user's Box account. This webhook will change the role for all collaborators on that item to 'viewer'.
4. Call `Cleanup` once per user. This webhook will finalize the migration and create a 'viewer' collaboration for the user on their managed folder.


# Endpoints

## Bootstrap (2-3s) 

**Purpose**: Prepare a managed folder to receive items owned by a migrated user. This will be called once per migrated user. 

**Assumptions**: Prior to running this script a desginated Box account has been created for holding all migrated user files. We call this the 'managed account'.

**Steps**:
1. Create an authenticated Box admin client.
2. Create an authenticated Box user client for the managed account identified by the `ManagedUserId`.
3. Lookup the user identified by the `UserLogin` via the Box admin client.
4. Generate a name for the managed folder that will hold the contents of the user's Box account.
5. Create the managed folder via the Box managed account client.
6. Create a group named for the user via the Box admin client.
7. Add the user to the group.
8. Create an 'editor' collaboration for the group on the managed folder. (A collaboration is created with the group rather than directly with the user in order to avoid user notifications, and to sidestep a user's preference to not auto-accept collaborations.)
9. Return the ID of the Box user's account (as `UserId`) and the ID of the user's managed folder (as `ManagedFolderId`) to the caller.

**HTTP Method**: `POST`

**Request Body Format**:  
```
{ 
  “UserLogin”: “Login of the migrated account.”, 
  “ManagedUserId”: “ID of the UITS-managed account for long-term storage.”, 
} 
```

**Response Status Code**: 200 (OK) with response body, or error information.
 
**Response Format**:
```
{ 
  “UserId”: “ID of the migrated account.”, 
  “ManagedFolderId”: “ID of the folder holding the migrated account’s items.”, 
}
``` 

## MoveItem 

**Purpose**: Move a single top-level file or folder from a migrated account to a long-term storage account. This will (likely) be called many times per migrated user. 

**Steps**:
1. Create a Box user client for the migrated user identified by `UserId`.
2. Get the Box item identified by `ItemId` using the Box user client.
3. Move the item to the managed folder identified by `ManagedFolderId` by updating it's `Parent` property. (If the item's `Parent` property already shows it's in in the managed folder, nothing needs to be done.) Note that while this 'move' operation appears to finish immediately, it may actually take Box some time to perform the data transfer.

**HTTP Method**: `POST`

**Request Body Format**:  
```
{ 
  “UserId”: “ID of the migrated account.”, 
  “ItemId”: “ID of the migrated top-level file/folder.”, 
  “ItemType”: “The type of item. Must be ‘file’ or ‘folder’.”, 
  “ManagedFolderId”: “ID of the folder holding the migrated account’s items.” 
} 
```

**Response Status Code**: 200 (OK) or error information.

## UpdateCollaborations 

**Purpose**: Update collaboration role on a single item to ‘viewer’ for all collaborators. This will (likely) be called many times per migrated user. 

**Steps**: 
1. Create a Box user client for the migrated user identified by `UserId`.
2. Fetch all collaborations on the Box item identified by `ItemId`.
3. Filter out any collaborations meeting the following criteria:
    * the collaboration is already in the `viewer` role
    * the ID of the `item` on which the collaboration was created does not match the `ItemId`. (This can happen if the collaboration exists e.g. on a higher-level folder.)
4. Change all the role of all remaining collaborations to `viewer`.

**HTTP Method**: POST 

**Request Body Format**:  
```
{ 
  “UserId”: “ID of the migrated account.”, 
  “ItemId”: “ID of the migrated top-level file/folder.”, 
  “ItemType”: “The type of item. Must be ‘file’ or ‘folder’.”, 
  “ManagedUserId”: “ID of the UITS-managed account for long-term storage.” 
}
``` 

**Response Status Code**: 200 (OK) or error information.

## Cleanup 

**Purpose**: Finalize the managed folder by making the migrated user a viewer collaborator on all content. This will be called once per migrated user. 

**Steps**: 
1. Create an authenticated Box admin client.
2. Create an authenticated Box user client for the managed account identified by the `ManagedUserId`.
3. Use the Box admin client to find all group memberships for the migrated user identified by `UserId`.
4. Find the migrated user's migration group and delete it with the Box admin client.
5. Use the Box managed user client to create a `viewer` collaboration on the folder identified by `ManagedFolderId` and the migrated user identified by `UserId`.

**Method**: POST 

**Request Body Format**:  
```
{ 
  “UserId”: “ID of the migrated account.”, 
  “ManagedUserId”: “ID of the UITS-managed account for long-term storage.”, 
  “ManagedFolderId”: “ID of the folder holding the migrated account’s items.” 
} 
```

**Response Status Code**: 200 (OK) or error information.

## ListSubfolders 

**Purpose**: List all subfolder names and ids of a given folder. 

**HTTP Method**: POST 

**Request Body Format**:  
```
{ 
  “UserLogin”: “Login of the migrated account.”, 
  “FolderId”: “ID of the folder for which to list subfolders.”, 
} 
```
**Response Status Code**: 200 (OK) with response body, or error information.

**Response Format**:
```
[ 
  { 
    “Name”: “Subfolder 1 name.”, 
    “ID”: “Subfolder 1 ID.” 
  }, 
  { 
    “Name”: “Subfolder 2 name.”, 
    “ID”: “Subfolder 2 ID.” 
  } 
]
```

# Box Enterprise Account Rollout Webhook

# Endpoint

## AccountMigration

We used [Azure Durable Functions](https://docs.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-overview?tabs=csharp) for this implementation.

**Purpose**: Convert Box enterprise acount to personal account.

**Funcitonal Flow Steps**:
1. MigrationOrchestrator:
    - Make calls to all activity functions.
2. SetAccountStatusToActive:
    - Create an authenticated Box admin client.
    - Use the admin client to set the user account status to `activate`.
3. GetBoxItemsToRemove:
    - Create an authenticated Box user client for the user identified by `UserId`.
    - Use user client to list the items in account root and fetch all the Box items identified by `ItemId`.  
    - Use user client to fetch all collaborations on the Box item identified by `ItemId`. 
4. RemoveItem - Permanently delete any data owned by the given IU User.
    - Create an authenticated Box user client for the user identified by `UserId`.  
    - Use user client to permanently delete Box items (file, folder, collaboration) identified by `ItemId`.
5. ListAllTheTrashedItems: 
    - Create an authenticated Box user client for the user identified by `UserId`.
    - Use user client to list all the trashed Box items.  
6. PurgeTrashedItem:
    - Create an authenticated Box user client for the user identified by `UserId`.  
    - Use user client to delete all the listed Box trashed items identified by `ItemId`.
7. SetPersonalAccountQuota: 
    - Create an authenticated Box admin client.
    - Use the admin client to set the user personal account quota to 50.0 GB.
8. RollAccountOutOfEnterprise:
    - Use admin token to make a HTTP PUT request to `https://api.box.com/2.0/users/:user_id/` to change the Enterprise value on the user account.


**HTTP Method**: `POST`

**Request Body Format**:  
```
{ 
  “UserEmail”: “Login of the user account.”,
} 
```

**Response Status Code**: 202 (Accepted) with response body, or error information.
 
**Response Format**:
```
{ 
  “Id”: “The ID of the orchestration instance.”, 
  “StatusQueryGetUri”: “The status URL of the orchestration instance - this will return 200 (OK) with response body”, 
  “SendEventPostUri”: “The "raise event" URL of the orchestration instance.”, 
  “TerminatePostUri”: “The "terminate" URL of the orchestration instance.”, 
  “PurgeHistoryDeleteUri”: “The "purge history" URL of the orchestration instance.” 
}
``` 

**StatusQueryGetUri Response Format**:
```
{ 
  “Name”: “The name of the orchestrator function to start.”, 
  “InstanceId”: “The ID of the orchestration instance.”, 
  “RuntimeStatus”: “The runtime status of the instance”, 
  “Input”: “The input of the function as a JSON value.”, 
  “CustomStatus”: “Custom orchestration status in JSON format.”,
  “Output”: “The output of the function”,
  “CreatedTime”: “The time at which the orchestrator function started running.”,
  “LastUpdatedTime”: “The time at which the orchestration last checkpointed.” 
}
``` 
