        
Walk the user's folder tree and capture any file comments to a local file.
The local file will be organized and named for the Box file. For example,
a Box file name "foo.txt" will have a comments file called "foo.txt.csv".

Required variables: 
    * Box user ID 
    * Box folder ID 
    * Path to a local folder

The following information will be captured for each file:
    * comment timestamp
    * commenter name (first last)
    * commenter login (user@iu.edu)
    * comment text/message