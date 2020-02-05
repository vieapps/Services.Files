# Services.Files

## Main parts:

- Managing all files and attachments of all microservices in the VIEApps NGX
- Allow sync across all well-known nodes
- Support pause/resume on download

## Supporting parts (Http.Storages):

- Work as a front-end of the specified directories
- The directories can be seperated by each account

## Others:

- Messaging protocol: WAMP-proto with WampSharp
- Authentication mechanisim: JSON Web Token
- .NET Core 2.x/3.x