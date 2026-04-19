# All three wrapping forms

## Form A — complete file

```csharp
using System;

public class FormASample
{
    public void Run()
    {
        Console.WriteLine("form A");
    }
}
```

## Form B — top-level statements

```csharp
System.Console.WriteLine("form B");
var n = 1 + 2;
System.Console.WriteLine(n);
```

## Form C — method body

```csharp
var s = "form C";
System.Console.WriteLine(s);
await System.Threading.Tasks.Task.CompletedTask;
```
