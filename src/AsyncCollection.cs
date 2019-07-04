using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncCollection
{
    /// <summary>
    /// Unbounded thread-safe queue which you can await on when dequeueing.
    /// Also implements IAsyncEnumerable, you use `async foreach` to retrieve all items in the queue.
    /// </summary>
    /// <typeparam name="T">Specifies the type of elements in the collection.</typeparam>
    public class AsyncCollection<T> : IDisposable
#if NETCOREAPP3_0
        ,IAsyncEnumerable<T>
#endif
    {
        private const int COMPLETE_ADDING_ON_MASK = unchecked((int)0x80000000);

        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(0);
        private readonly ConcurrentQueue<T> queue = new ConcurrentQueue<T>();
        private readonly CancellationTokenSource doneCancellationToken = new CancellationTokenSource();
        private volatile int currentAdders = 0;
        private bool isDisposed = false;

        /// <summary>Gets whether this AsyncCollection has been marked as complete for adding and is empty.</summary>
        /// <value>Whether this collection has been marked as complete for adding and is empty.</value>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        public bool IsCompleted
        {
            get
            {
                CheckDisposed();
                return (IsAddingCompleted && (semaphore.CurrentCount == 0));
            }
        }

        /// <summary>Gets whether this AsyncCollection has been marked as complete for adding.</summary>
        /// <value>Whether this collection has been marked as complete for adding.</value>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        public bool IsAddingCompleted
        {
            get
            {
                CheckDisposed();
                return currentAdders == COMPLETE_ADDING_ON_MASK;
            }
        }

        /// <summary>Gets the number of items contained in the AsyncCollection.</summary>
        /// <value>The number of items contained in the AsyncCollection.</value>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        public int Count
        {
            get
            {
                CheckDisposed();
                return semaphore.CurrentCount;
            }
        }

        /// <summary>
        /// Adds the item to the AsyncCollection.
        /// </summary>
        /// <param name="item">The item to be added to the collection. The value can be a null reference.</param>
        /// <exception cref="T:System.InvalidOperationException">The AsyncCollection has been marked as complete with regards to additions.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        public void Add(T item)
        {
            CheckDisposed();

            SpinWait spinner = new SpinWait();
            while (true)
            {
                int observedAdders = currentAdders;
                if ((observedAdders & COMPLETE_ADDING_ON_MASK) != 0)
                {
                    spinner.Reset();
                    while (currentAdders != COMPLETE_ADDING_ON_MASK) spinner.SpinOnce();
                    throw new InvalidOperationException("Adding is completed");
                }
                if (Interlocked.CompareExchange(ref currentAdders, observedAdders + 1, observedAdders) == observedAdders)
                {
                    break;
                }
                spinner.SpinOnce();
            }


            queue.Enqueue(item);
            semaphore.Release();

            Interlocked.Decrement(ref currentAdders);
        }

        /// <summary>
        /// Marks the AsyncCollection instance as not accepting any more additions.
        /// </summary>
        /// <remarks>
        /// After a collection has been marked as complete for adding, adding to the collection is not permitted
        /// and attempts to remove from the collection will not wait when the collection is empty.
        /// </remarks>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        public void CompleteAdding()
        {
            CheckDisposed();

            if (IsAddingCompleted)
                return;

            SpinWait spinner = new SpinWait();
            while (true)
            {
                int observedAdders = currentAdders;
                if ((observedAdders & COMPLETE_ADDING_ON_MASK) != 0)
                {
                    spinner.Reset();
                    while (currentAdders != COMPLETE_ADDING_ON_MASK) spinner.SpinOnce();
                    return;
                }

                if (Interlocked.CompareExchange(ref currentAdders, observedAdders | COMPLETE_ADDING_ON_MASK, observedAdders) == observedAdders)
                {
                    spinner.Reset();
                    while (currentAdders != COMPLETE_ADDING_ON_MASK) spinner.SpinOnce();

                    if (queue.Count == 0)
                        doneCancellationToken.Cancel();

                    return;
                }
                spinner.SpinOnce();
            }
        }


        private async Task<(bool, T)> TryTakeAsync(CancellationToken cancellationToken, CancellationToken linked)
        {
            if (IsCompleted)
            {
                return (false, default(T));
            }

            if (semaphore.Wait(0))
            {
                if (queue.TryDequeue(out T result))
                    return (true, result);

                throw new InvalidOperationException();
            }

            try
            {
                await semaphore.WaitAsync(linked).ConfigureAwait(false);
                if (queue.TryDequeue(out T result))
                    return (true, result);

                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Completed, exit loop
                return (false, default(T));
            }
        }

        /// <summary>Takes an item from the AsyncCollection.</summary>
        /// <returns>The item removed from the collection.</returns>
        /// <exception cref="T:System.InvalidOperationException">The AsyncCollection is empty and has been marked
        /// as complete with regards to additions.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        public T Take(CancellationToken cancellationToken = new CancellationToken())
        {
            if (TryTake(out T result, Timeout.Infinite, cancellationToken))
                return result;

            throw new InvalidOperationException("Can't take when done");
        }

        /// <summary>
        /// Attempts to remove an item from the AsyncCollection.
        /// A <see cref="System.OperationCanceledException"/> is thrown if the <see cref="CancellationToken"/> is
        /// canceled.
        /// </summary>
        /// <param name="result">The item removed from the collection.</param>
        /// <param name="timeout">A <see cref="System.TimeSpan"/> that represents the number of milliseconds
        /// to wait, or a <see cref="System.TimeSpan"/> that represents -1 milliseconds to wait indefinitely.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>true if an item could be removed from the collection within
        /// the alloted time; otherwise, false.</returns>
        /// <exception cref="OperationCanceledException">If the <see cref="CancellationToken"/> is canceled.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The <see
        /// cref="T:System.Collections.Concurrent.BlockingCollection{T}"/> has been disposed.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="timeout"/> is a negative number
        /// other than -1 milliseconds, which represents an infinite time-out -or- timeout is greater than
        /// <see cref="System.Int32.MaxValue"/>.</exception>

        public bool TryTake(out T result, TimeSpan timeout, CancellationToken cancellationToken = new CancellationToken())
        {
            return TryTake(out result, (int) timeout.TotalMilliseconds, cancellationToken);
        }

        /// <summary>
        /// Attempts to remove an item from the AsyncCollection.
        /// A <see cref="System.OperationCanceledException"/> is thrown if the <see cref="CancellationToken"/> is
        /// canceled.
        /// </summary>
        /// <param name="result">The item removed from the collection.</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see
        /// cref="System.Threading.Timeout.Infinite"/> (-1) to wait indefinitely.</param>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>true if an item could be removed from the collection within
        /// the alloted time; otherwise, false.</returns>
        /// <exception cref="OperationCanceledException">If the <see cref="CancellationToken"/> is canceled.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a
        /// negative number other than -1, which represents an infinite time-out.</exception>
        public bool TryTake(out T result, int millisecondsTimeout, CancellationToken cancellationToken = new CancellationToken())
        {
            CheckDisposed();

            CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(doneCancellationToken.Token, cancellationToken);

            try
            {
                if (IsCompleted)
                {
                    result = default(T);
                    return false;
                }

                if (semaphore.Wait(0))
                {
                    if (queue.TryDequeue(out result))
                        return true;

                    throw new InvalidOperationException();
                }

                try
                {
                    if (semaphore.Wait(millisecondsTimeout, linked.Token))
                    {
                        if (queue.TryDequeue(out result))
                            return true;

                        throw new InvalidOperationException();
                    }
                    else
                    {
                        result = default(T);
                        return false;
                    }
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    result = default(T);
                    return false;
                }
            }
            finally
            {
                linked.Dispose();
            }
        }

        /// <summary>Takes an item from the AsyncCollection asynchronously.</summary>
        /// <returns>The item removed from the collection.</returns>
        /// <exception cref="T:System.InvalidOperationException">The AsyncCollection is empty and has been marked
        /// as complete with regards to additions.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        public async Task<T> TakeAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            CheckDisposed();

            CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(doneCancellationToken.Token, cancellationToken);

            try
            {
                var (success, result) = await TryTakeAsync(cancellationToken, linked.Token).ConfigureAwait(false);

                if (success)
                    return result;

                throw new InvalidOperationException("Can't take when done");
            }
            finally
            {
                linked.Dispose();
            }
        }

        /// <summary>Provides a consuming <see cref="T:System.Collections.Generics.IEnumerable{T}"/> for items in the collection.</summary>
        /// <returns>An <see cref="T:System.Collections.Generics.IEnumerable{T}"/> that removes and returns items from the collection.</returns>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        public IEnumerable<T> GetConsumingEnumerable()
        {
            return GetConsumingEnumerable(CancellationToken.None);
        }

        /// <summary>Provides a consuming <see cref="T:System.Collections.Generics.IEnumerable{T}"/> for items in the collection.
        /// Calling MoveNext on the returned enumerable will block if there is no data available, or will
        /// throw an <see cref="System.OperationCanceledException"/> if the <see cref="CancellationToken"/> is canceled.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>An <see cref="T:System.Collections.Generics.IEnumerable{T}"/> that removes and returns items from the collection.</returns>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        /// <exception cref="OperationCanceledException">If the <see cref="CancellationToken"/> is canceled.</exception>
        public IEnumerable<T> GetConsumingEnumerable(CancellationToken cancellationToken)
        {
            CheckDisposed();

            CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(doneCancellationToken.Token, cancellationToken);

            try
            {
                while (true)
                {
                    if (TryTake(out T result, Timeout.Infinite, linked.Token))
                        yield return result;
                    else
                        break;
                }
            }
            finally
            {
                linked.Dispose();
            }
        }


#if NETCOREAPP3_0

        /// <summary>
        /// Provides a consuming IAsyncEnumerator<T> for items in the collection.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>An IAsyncEnumerator<T> that removes and returns items from the collection.</returns>
        /// <exception cref="T:System.ObjectDisposedException">The AsyncCollection has been disposed.</exception>
        /// <exception cref="OperationCanceledException">If the <see cref="CancellationToken"/> is canceled.</exception>
        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = new CancellationToken())
        {
            CheckDisposed();

            CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(doneCancellationToken.Token, cancellationToken);

            try
            {
                while (true)
                {
                    var (success, result) = await TryTakeAsync(cancellationToken, linked.Token).ConfigureAwait(false);

                    if (success)
                        yield return result;
                    else
                        break;
                }
            }
            finally
            {
                linked.Dispose();
            }
        }

        #endif

        ~AsyncCollection()
        {
            Dispose();
        }

        /// <summary>
        /// Releases resources used by the AsyncCollection instance.
        /// </summary>
        public void Dispose()
        {
            if (!isDisposed)
            {
                semaphore.Dispose();
                doneCancellationToken.Dispose();
                GC.SuppressFinalize(this);
                isDisposed = true;
            }
        }

        private void CheckDisposed()
        {
            if (isDisposed)
            {
                throw new ObjectDisposedException("AsyncCollection");
            }
        }
    }
}
