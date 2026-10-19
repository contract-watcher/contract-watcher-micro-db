![build](https://github.com/contract-watcher/contract-watcher-micro-db/actions/workflows/build.yml/badge.svg)
![release](https://github.com/contract-watcher/contract-watcher-micro-db/actions/workflows/release.yml/badge.svg)
[![NuGet](https://img.shields.io/nuget/v/ContractWatcher.MicroDb.svg)](https://www.nuget.org/packages/ContractWatcher.MicroDb)
[![codecov](https://codecov.io/gh/contract-watcher/contract-watcher-micro-db/branch/main/graph/badge.svg)](https://codecov.io/gh/contract-watcher/contract-watcher-micro-db)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://opensource.org/licenses/MIT)

## Quick start

Установить пакет можно с помощью команды

```bash
dotnet add package ContractWatcher.MicroDb
```

### 1. Сущность

```csharp
public sealed record User(string Id, string Name);
```

### 2. Контекст

```csharp
using ContractWatcher.MicroDb;
using ContractWatcher.MicroDb.Options;

public sealed class AppDbContext : MicroDbContext
{
    public MicroDbSet<User, string> Users { get; }

    public AppDbContext()
        : base(new MicroDbOptions { DirectoryPath = "./data" })
    {
        Users = Set<User, string>("users", user => user.Id);
    }
}
```

### 3. Использование

```csharp
using var db = new AppDbContext();

db.Users.Upsert(new User("1", "Danil"));
await db.SaveChangesAsync();

var user = db.Users.Find("1");
Console.WriteLine(user?.Name);
```

Данные сохраняются в `./data/users.json` и загружаются при следующем создании контекста.