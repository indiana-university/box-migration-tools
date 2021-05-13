This console app will mass-update all users in your enterprise to a chosen status (active, inactive, etc.)

Requires a Box JWT app with `Manage Users` permissions. The contents of the [Box JWT App configuration JSON](https://developer.box.com/guides/authentication/jwt/with-sdk/#prerequisites) file should be saved as a user secret called `BoxConfigJson`. 

Some settings you can change in `Program.cs`:  

* `BoxStatus`: The Box user [status](https://developer.box.com/reference/put-users-id/#param-status) to which you want to update the accounts.
* `LogFilePath`: Path to a log file on your local file system.
* `ExclusionList`: An array of usernames or logins to exclude from the status update.