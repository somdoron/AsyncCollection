# AsyncCollection

AsyncCollection is an unbounded queue which you can asynchronously (using await) dequeue items from the queue.

AsyncCollection also implement C# 8.0 IAsyncEnumerable, which allow asynchronously iterating the queue using `await foreach`.

API of the colleciton is very similar to BlockingCollection.

## Download

Install using nuget: https://www.nuget.org/packages/AsyncCollection/.

## Example

Iterating the collection asynchronously:

```csharp
AsyncCollection<int> collection = new AsyncCollection<int>();

var t = Task.Run(async () =>
{
    while (!collection.IsCompleted)
    {
        var item = await collection.TakeAsync();
        
        // process
    }
});

for (int i = 0; i < 1000; i++)
{
    collection.Add(i);
}

collection.CompleteAdding();

t.Wait();
```

Iterating the collection asynchronously using IAsyncEnumerable, require dotnet core 3.0:

```csharp
AsyncCollection<int> collection = new AsyncCollection<int>();

var t = Task.Run(async () =>
{
    await foreach (var item in collection)
    {
        // process
    }
});

for (int i = 0; i < 1000; i++)
{
    collection.Add(i);
}

collection.CompleteAdding();

t.Wait();
```
