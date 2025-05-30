# Microsoft Orleans Persistence for Azure Cosmos DB

## Introduction
Microsoft Orleans Persistence for Azure Cosmos DB provides grain persistence for Microsoft Orleans using Azure Cosmos DB. This allows your grains to persist their state in Azure Cosmos DB and reload it when they are reactivated, offering a globally distributed, multi-model database service for your Orleans applications.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Persistence.Cosmos
```

## Example - Configuring Azure Cosmos DB Persistence
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Azure Cosmos DB as grain storage
            .AddCosmosGrainStorage(
                name: "cosmosStore",
                configureOptions: options =>
                {
                    options.AccountEndpoint = "https://YOUR_COSMOS_ENDPOINT";
                    options.AccountKey = "YOUR_COSMOS_KEY";
                    options.DB = "YOUR_DATABASE_NAME";
                    options.CanCreateResources = true;
                });
    });

// Run the host
await builder.RunAsync();
```

## Example - Using Grain Storage in a Grain
```csharp
// Define grain state class

public class MyGrainState
{
    public string Data { get; set; }
    public int Version { get; set; }
}

// Grain implementation that uses the Cosmos DB storage
public class MyGrain : Grain, IMyGrain, IGrainWithStringKey
{
    private readonly IPersistentState<MyGrainState> _state;

    public MyGrain([PersistentState("state", "cosmosStore")] IPersistentState<MyGrainState> state)
    {
        _state = state;
    }

    public async Task SetData(string data)
    {
        _state.State.Data = data;
        _state.State.Version++;
        await _state.WriteStateAsync();
    }

    public Task<string> GetData()
    {
        return Task.FromResult(_state.State.Data);
    }
}
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Grain Persistence](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-persistence)
- [Azure Storage Providers](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-persistence/azure-storage)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)