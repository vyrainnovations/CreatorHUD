// Lets C# await Windows Runtime operations when building against the split metadata files in
// System32\WinMetadata (no Windows SDK installed). The .NET Framework's own helpers in
// System.Runtime.WindowsRuntime.dll are bound to the SDK's single Windows.winmd, so the compiler
// cannot use them here; these do the same job with a TaskCompletionSource.

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Foundation;

static class WinRtAwait
{
    public static Task<T> AsTask<T>(this IAsyncOperation<T> op)
    {
        var tcs = new TaskCompletionSource<T>();
        op.Completed = (o, status) =>
        {
            if (status == AsyncStatus.Completed)
            {
                try { tcs.TrySetResult(o.GetResults()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }
            else if (status == AsyncStatus.Canceled) tcs.TrySetCanceled();
            else tcs.TrySetException(o.ErrorCode ?? new Exception("WinRT operation failed"));
        };
        return tcs.Task;
    }

    public static Task AsTask(this IAsyncAction op)
    {
        var tcs = new TaskCompletionSource<bool>();
        op.Completed = (o, status) =>
        {
            if (status == AsyncStatus.Completed)
            {
                try { o.GetResults(); tcs.TrySetResult(true); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }
            else if (status == AsyncStatus.Canceled) tcs.TrySetCanceled();
            else tcs.TrySetException(o.ErrorCode ?? new Exception("WinRT action failed"));
        };
        return tcs.Task;
    }

    // Copies a small WinRT stream (album art, app logos) into memory as a .NET stream.
    public static System.IO.Stream ToMemoryStream(this Windows.Storage.Streams.IRandomAccessStreamWithContentType s)
    {
        uint size = (uint)s.Size;
        var bytes = new byte[size];
        if (size > 0)
        {
            using (var reader = new Windows.Storage.Streams.DataReader(s.GetInputStreamAt(0)))
            {
                uint got = reader.LoadAsync(size).AsTask().Result;
                if (got < size) Array.Resize(ref bytes, (int)got);
                reader.ReadBytes(bytes);
            }
        }
        return new System.IO.MemoryStream(bytes);
    }

    public static TaskAwaiter<T> GetAwaiter<T>(this IAsyncOperation<T> op) { return op.AsTask().GetAwaiter(); }
    public static TaskAwaiter GetAwaiter(this IAsyncAction op) { return op.AsTask().GetAwaiter(); }
}
