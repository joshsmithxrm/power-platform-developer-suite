# Fixture guide with a bad C# block

The block below references a type that does not exist. The workflow's smoke
step must report the path + line.

```csharp
var thing = new ThisTypeDoesNotExistInTheAssemblies();
Console.WriteLine(thing);
```
