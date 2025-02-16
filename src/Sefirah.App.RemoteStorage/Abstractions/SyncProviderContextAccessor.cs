﻿namespace Sefirah.App.RemoteStorage.Abstractions;
public class SyncProviderContextAccessor : ISyncProviderContextAccessor
{
    private static SyncProviderContext? _staticContext;
    private static readonly AsyncLocal<ContextHolder> _syncProviderContextCurrent = new();

    /// <inheritdoc/>
    public SyncProviderContext Context
    {
        get
        {
            return _syncProviderContextCurrent.Value?.Context ?? _staticContext ?? 
                throw new NullReferenceException("SyncProviderContext not initialized");
        }
        set
        {
            _staticContext = value; // Keep a static copy for shell extensions
            
            var holder = _syncProviderContextCurrent.Value;
            if (holder != null)
            {
                // Clear current SyncProviderContext trapped in the AsyncLocals, as its done.
                holder.Context = null;
            }

            if (value != null)
            {
                // Use an object indirection to hold the SyncProviderContext in the AsyncLocal,
                // so it can be cleared in all ExecutionContexts when its cleared.
                _syncProviderContextCurrent.Value = new ContextHolder { Context = value };
            }
        }
    }

    private sealed class ContextHolder
    {
        public SyncProviderContext? Context;
    }
}
