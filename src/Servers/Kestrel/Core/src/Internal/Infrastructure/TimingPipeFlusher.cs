// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure
{
    /// <summary>
    /// This wraps PipeWriter.FlushAsync() in a way that allows multiple awaiters making it safe to call from publicly
    /// exposed Stream implementations while also tracking response data rate.
    /// </summary>
    internal class TimingPipeFlusher
    {
        private readonly PipeWriter _writer;
        private readonly ITimeoutControl _timeoutControl;
        private readonly IKestrelTrace _log;

        private readonly object _flushLock = new object();
        private Task<FlushResult> _lastFlushTask = null;

        public TimingPipeFlusher(
            PipeWriter writer,
            ITimeoutControl timeoutControl,
            IKestrelTrace log)
        {
            _writer = writer;
            _timeoutControl = timeoutControl;
            _log = log;
        }

        public ValueTask<FlushResult> FlushAsync()
        {
            return FlushAsync(outputAborter: null, cancellationToken: default);
        }

        public ValueTask<FlushResult> FlushAsync(IHttpOutputAborter outputAborter, CancellationToken cancellationToken)
        {
            return FlushAsync(minRate: null, count: 0, outputAborter: outputAborter, cancellationToken: cancellationToken);
        }

        public ValueTask<FlushResult> FlushAsync(MinDataRate minRate, long count)
        {
            return FlushAsync(minRate, count, outputAborter: null, cancellationToken: default);
        }

        public ValueTask<FlushResult> FlushAsync(MinDataRate minRate, long count, IHttpOutputAborter outputAborter, CancellationToken cancellationToken)
        {
            // https://github.com/dotnet/corefxlab/issues/1334
            // Pipelines don't support multiple awaiters on flush.
            lock (_flushLock)
            {
                if (_lastFlushTask != null && !_lastFlushTask.IsCompleted)
                {
                    _lastFlushTask = AwaitLastFlushAndTimeFlushAsync(_lastFlushTask, minRate, count, outputAborter, cancellationToken);
                    return new ValueTask<FlushResult>(_lastFlushTask);
                }

                return TimeFlushAsync(minRate, count, outputAborter, cancellationToken);
            }
        }

        private ValueTask<FlushResult> TimeFlushAsync(MinDataRate minRate, long count, IHttpOutputAborter outputAborter, CancellationToken cancellationToken)
        {
            var pipeFlushTask = _writer.FlushAsync(cancellationToken);

            if (minRate != null)
            {
                _timeoutControl.BytesWrittenToBuffer(minRate, count);
            }

            if (pipeFlushTask.IsCompletedSuccessfully)
            {
                return new ValueTask<FlushResult>(pipeFlushTask.Result);
            }

            _lastFlushTask = TimeFlushAsyncAwaited(pipeFlushTask, minRate, count, outputAborter, cancellationToken);
            return new ValueTask<FlushResult>(_lastFlushTask);
        }

        private async Task<FlushResult> TimeFlushAsyncAwaited(ValueTask<FlushResult> pipeFlushTask, MinDataRate minRate, long count, IHttpOutputAborter outputAborter, CancellationToken cancellationToken)
        {
            if (minRate != null)
            {
                _timeoutControl.StartTimingWrite();
            }

            try
            {
                return await pipeFlushTask;
            }
            catch (OperationCanceledException ex) when (outputAborter != null)
            {
                outputAborter.Abort(new ConnectionAbortedException(CoreStrings.ConnectionOrStreamAbortedByCancellationToken, ex));
            }
            catch (Exception ex)
            {
                // A canceled token is the only reason flush should ever throw.
                _log.LogError(0, ex, $"Unexpected exception in {nameof(TimingPipeFlusher)}.{nameof(TimeFlushAsync)}.");
            }
            finally
            {
                if (minRate != null)
                {
                    _timeoutControl.StopTimingWrite();
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            return default;
        }

        private async Task<FlushResult> AwaitLastFlushAndTimeFlushAsync(Task lastFlushTask, MinDataRate minRate, long count, IHttpOutputAborter outputAborter, CancellationToken cancellationToken)
        {
            await lastFlushTask;
            return await TimeFlushAsync(minRate, count, outputAborter, cancellationToken);
        }
    }
}
