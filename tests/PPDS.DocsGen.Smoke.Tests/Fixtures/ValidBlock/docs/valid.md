# Valid fenced block

Form C — method body. Uses only System.* types so the block compiles
against nothing more than the runtime reference set.

```csharp
var xs = new List<int> { 1, 2, 3 };
var total = 0;
foreach (var x in xs)
{
    total += x;
}

Console.WriteLine(total);
await Task.CompletedTask;
```
