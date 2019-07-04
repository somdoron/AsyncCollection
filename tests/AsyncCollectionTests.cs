using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AsyncCollection.Tests
{
    [TestClass]
    public class AsyncCollectionTests
    {
        [TestMethod]
        public async Task AsyncTakeWithDelay()
        {
            AsyncCollection<int> collection = new AsyncCollection<int>();

            Task.Run(() =>
            {
                Thread.Sleep(100);
                collection.Add(1);
            });

            int item = await collection.TakeAsync();
            Assert.AreEqual(item, 1);
        }

        [TestMethod]
        public void TakeWithDelay()
        {
            AsyncCollection<int> collection = new AsyncCollection<int>();

            Task.Run(() =>
            {
                Thread.Sleep(100);
                collection.Add(1);
            });

            int item = collection.Take();
            Assert.AreEqual(item, 1);
        }

        [TestMethod]
        public void TryTakeWithElapsedTimeout()
        {
            AsyncCollection<int> collection = new AsyncCollection<int>();

            bool success = collection.TryTake(out int result, 100);

            Assert.IsFalse(success);
        }

        [TestMethod]
        public void ConsumeSyncFromMultipleThreads()
        {
            AsyncCollection<int> collection = new AsyncCollection<int>();
            int t1Counter = 0;
            int t2Counter = 0;

            var t1 = Task.Run(() =>
            {
                foreach (var item in collection.GetConsumingEnumerable())
                {
                    t1Counter++;
                }
            });

            var t2 = Task.Run(() =>
            {
                foreach (var item in collection.GetConsumingEnumerable())
                {
                    t2Counter++;
                }
            });

            for (int i = 0; i < 1000; i++)
            {
                collection.Add(i);
            }

            collection.CompleteAdding();

            t1.Wait();
            t2.Wait();

            Assert.IsTrue(t1Counter > 400);
            Assert.IsTrue(t2Counter > 400);
        }

        [TestMethod]
        public async Task CancelTake()
        {
            AsyncCollection<int> collection = new AsyncCollection<int>();

            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource())
            {
                var t = Task.Run(async () => await collection.TakeAsync(cancellationTokenSource.Token));

                cancellationTokenSource.Cancel();

                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => t);
            }
        }

        [TestMethod]
        public async Task ProduceFromMultipleThreads()
        {
            AsyncCollection<int> collection = new AsyncCollection<int>();

            var t1 = Task.Run(() =>
            {
                for (int i = 0; i < 500; i++)
                {
                    collection.Add(i*2);
                }
            });

            var t2 = Task.Run(() =>
            {
                for (int i = 0; i < 500; i++)
                {
                    collection.Add(i*2 + 1);
                }
            });

            Task.WhenAll(t1, t2).ContinueWith(t => collection.CompleteAdding());

            int counter = 0;
            await foreach (var item in collection)
            {
                counter++;
            }

            Assert.AreEqual(counter, 1000);
        }

        [TestMethod]
        public void TakeAsyncFromMultipleThreads()
        {
            AsyncCollection<int> collection = new AsyncCollection<int>();
            int t1Counter = 0;
            int t2Counter = 0;

            var t1 = Task.Run(async () =>
            {
                while (!collection.IsCompleted)
                {
                    try
                    {
                        await collection.TakeAsync();
                        t1Counter++;
                    }
                    catch (InvalidOperationException)
                    {

                    }
                }
            });

            var t2 = Task.Run(async () =>
            {
                while (!collection.IsCompleted)
                {
                    try
                    {
                        await collection.TakeAsync();
                        t2Counter++;
                    }
                    catch (InvalidOperationException)
                    {

                    }
                }
            });

            for (int i = 0; i < 1000; i++)
            {
                collection.Add(i);
            }

            collection.CompleteAdding();

            t1.Wait();
            t2.Wait();

            Assert.IsTrue(t1Counter > 400);
            Assert.IsTrue(t2Counter > 400);
        }

        [TestMethod]
        public void ConsumeAsyncEnumerableFromMultipleThreads()
        {
            AsyncCollection<int> collection = new AsyncCollection<int>();
            int t1Counter = 0;
            int t2Counter = 0;

            var t1 = Task.Run(async () =>
            {
                await foreach (var item in collection)
                {
                    t1Counter++;
                }
            });

            var t2 = Task.Run(async () =>
            {
                await foreach (var item in collection)
                {
                    t2Counter++;
                }
            });

            for (int i = 0; i < 1000; i++)
            {
                collection.Add(i);
            }

            collection.CompleteAdding();

            t1.Wait();
            t2.Wait();

            Assert.IsTrue(t1Counter > 400);
            Assert.IsTrue(t2Counter > 400);
        }
    }
}
