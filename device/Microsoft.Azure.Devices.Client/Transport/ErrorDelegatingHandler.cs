// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Devices.Client.Transport
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client.Exceptions;
    using Microsoft.Azure.Devices.Client.Extensions;

    sealed class ErrorDelegatingHandler : DefaultDelegatingHandler
    {

        internal static readonly HashSet<Type> TransientExceptions = new HashSet<Type>
        {
            typeof(IotHubClientTransientException),
            typeof(IotHubCommunicationException),
            typeof(ServerBusyException),
            typeof(IOException),
            typeof(TimeoutException),
            typeof(ObjectDisposedException),
            typeof(OperationCanceledException),
            typeof(TaskCanceledException),
            typeof(IotHubThrottledException),
#if !PCL && !WINDOWS_UWP
            typeof(System.Net.Sockets.SocketException),
#endif
        };

        internal static readonly HashSet<Type> TransportTransientExceptions = new HashSet<Type>
        {
            typeof(IotHubThrottledException),
            typeof(IotHubClientTransientException),
            typeof(ServerBusyException),
            typeof(OperationCanceledException),
            typeof(TaskCanceledException),
        };

        TaskCompletionSource<int> openCompletion;

        public ErrorDelegatingHandler(IPipelineContext context)
            : base(context)
        {
        }

        public override async Task OpenAsync(bool explicitOpen, CancellationToken cancellationToken)
        {
            TaskCompletionSource<int> openCompletionBeforeOperationStarted = Volatile.Read(ref this.openCompletion);
            if (openCompletionBeforeOperationStarted == null)
            {
                openCompletionBeforeOperationStarted = new TaskCompletionSource<int>();
                TaskCompletionSource<int> currentOpenPromise;
                if ((currentOpenPromise = Interlocked.CompareExchange(ref this.openCompletion, openCompletionBeforeOperationStarted, null)) == null)
                {
                    IDelegatingHandler handlerBeforeOperationStarted = this.ContinuationFactory(Context);
                    this.InnerHandler = handlerBeforeOperationStarted;
                    try
                    {
                        await this.ExecuteWithErrorHandlingAsync(() => base.OpenAsync(explicitOpen, cancellationToken), false, cancellationToken);
                        openCompletionBeforeOperationStarted.TrySetResult(0);
                    }
                    catch (Exception ex) when (IsTransportHandlerStillUsable(ex))
                    {
                        this.Reset(openCompletionBeforeOperationStarted, handlerBeforeOperationStarted);
                        throw;
                    }
                    catch (Exception ex) when (!ex.IsFatal())
                    {
                        throw;
                    }
                }
                else
                {
                    await currentOpenPromise.Task;
                }
            }
            else
            {
                await openCompletionBeforeOperationStarted.Task;
            }
        }

        public override Task<Message> ReceiveAsync(CancellationToken cancellationToken)
        {
            return this.ExecuteWithErrorHandlingAsync(() => base.ReceiveAsync(cancellationToken), true, cancellationToken);
        }

        public override Task<Message> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            return this.ExecuteWithErrorHandlingAsync(() => base.ReceiveAsync(timeout, cancellationToken), true, cancellationToken);
        }

        public override Task EnableMethodsAsync(CancellationToken cancellationToken)
        {
            return this.ExecuteWithErrorHandlingAsync(() => base.EnableMethodsAsync(cancellationToken), false, cancellationToken);
        }

        public override Task DisableMethodsAsync(CancellationToken cancellationToken)
        {
            return this.ExecuteWithErrorHandlingAsync(() => base.DisableMethodsAsync(cancellationToken), false, cancellationToken);
        }

        public override Task AbandonAsync(string lockToken, CancellationToken cancellationToken)
        {
            return this.ExecuteWithErrorHandlingAsync(() => base.AbandonAsync(lockToken, cancellationToken), true, cancellationToken);
        }

        public override Task CompleteAsync(string lockToken, CancellationToken cancellationToken)
        {
            return this.ExecuteWithErrorHandlingAsync(() => base.CompleteAsync(lockToken, cancellationToken), true, cancellationToken);
        }

        public override Task RejectAsync(string lockToken, CancellationToken cancellationToken)
        {
            return this.ExecuteWithErrorHandlingAsync(() => base.RejectAsync(lockToken, cancellationToken), true, cancellationToken);
        }

        public override Task SendEventAsync(IEnumerable<Message> messages, CancellationToken cancellationToken)
        {
            return this.ExecuteWithErrorHandlingAsync(() => base.SendEventAsync(messages, cancellationToken), true, cancellationToken);
        }

        public override Task SendEventAsync(Message message, CancellationToken cancellationToken)
        {
            return this.ExecuteWithErrorHandlingAsync(() => base.SendEventAsync(message, cancellationToken), true, cancellationToken);
        }

        public override Task SendMethodResponseAsync(MethodResponseInternal methodResponse, CancellationToken cancellationToken)
        {
            return this.ExecuteWithErrorHandlingAsync(() => base.SendMethodResponseAsync(methodResponse, cancellationToken), true, cancellationToken);
        }

        Task ExecuteWithErrorHandlingAsync(Func<Task> asyncOperation, bool ensureOpen, CancellationToken cancellationToken)
        {
            return ExecuteWithErrorHandlingAsync(async () => { await asyncOperation(); return 0; }, ensureOpen, cancellationToken);
        }

        async Task<T> ExecuteWithErrorHandlingAsync<T>(Func<Task<T>> asyncOperation, bool ensureOpen, CancellationToken cancellationToken)
        {
            if (ensureOpen)
            {
                await this.EnsureOpenAsync(cancellationToken);
            }

            TaskCompletionSource<int> openCompletionBeforeOperationStarted = Volatile.Read(ref this.openCompletion);
            IDelegatingHandler handlerBeforeOperationStarted = this.InnerHandler;

            try
            {
                return await asyncOperation();
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                if (IsTransient(ex))
                {
                    if (IsTransportHandlerStillUsable(ex))
                    {
                        if (ex is IotHubClientTransientException)
                        {
                            throw;
                        }
                        throw new IotHubClientTransientException("Transient error occured, please retry.", ex);
                    }
                    this.Reset(openCompletionBeforeOperationStarted, handlerBeforeOperationStarted);
                    if (ex is IotHubClientTransientException)
                    {
                        throw;
                    }
                    throw new IotHubClientTransientException("Transient error occured, please retry.", ex);
                }
                else
                {
                    this.Reset(openCompletionBeforeOperationStarted, handlerBeforeOperationStarted);
                    throw;
                }
            }
        }

        Task EnsureOpenAsync(CancellationToken cancellationToken)
        {
            return this.OpenAsync(false, cancellationToken);
        }

        static bool IsTransportHandlerStillUsable(Exception exception)
        {
            return exception.Unwind(true).Any(e => TransportTransientExceptions.Contains(e.GetType())) || IsThrottling(exception);
        }

        /// <summary>
        /// this is a hack and it should be fixed in one of next releases - we should rely on the exception type only.
        /// </summary>
        /// <param name="exception"></param>
        /// <returns></returns>
        internal static bool IsThrottling(Exception exception)
        {
            if (exception is IotHubClientTransientException)
            {
                if (exception.InnerException == null)
                {
                    return false;
                }
                
                //unwrap the internal exception
                exception = exception.InnerException;
            }

            if (exception is IotHubThrottledException)
            {
                return true;
            }

            if (exception is IotHubException)
            {
                return exception.Message.Contains("throttl"); //...e/...ing/...ed;
            }

            return false;
        }

        static bool IsTransient(Exception exception)
        {
            return exception.Unwind(true).Any(e => TransientExceptions.Contains(e.GetType())) || IsThrottling(exception);
        }

        void Reset(TaskCompletionSource<int> openCompletionBeforeOperationStarted, IDelegatingHandler handlerBeforeOperationStarted)
        {
            if (openCompletionBeforeOperationStarted == Volatile.Read(ref this.openCompletion))
            {
                if (Interlocked.CompareExchange(ref this.openCompletion, null, openCompletionBeforeOperationStarted) == openCompletionBeforeOperationStarted)
                {
                    Cleanup(handlerBeforeOperationStarted);
                }
            }
        }

        static async void Cleanup(IDelegatingHandler handler)
        {
            try
            {
                if (handler != null)
                {
                    await handler.CloseAsync();
                }
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                //unexpected behaviour - ignore. LOG?
            }
        }
    }
}