# Ignored fenced block

The opening fence carries `// ignore-smoke` in the info string so the
smoke tool must skip this block entirely — even though the body would
fail to compile on its own.

```csharp // ignore-smoke
var thing = new ThisTypeDoesNotExistEither();
Console.WriteLine(thing);
```
