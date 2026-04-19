# Invalid fenced block

This block references a type that does not exist. Smoke must report the
failure with the file path and a line number within the block.

```csharp
var thing = new ThisTypeDoesNotExist();
Console.WriteLine(thing);
```
