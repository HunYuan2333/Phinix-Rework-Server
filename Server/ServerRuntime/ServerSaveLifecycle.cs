using System;
using System.Threading;
using Stateless;

namespace ServerRuntime
{
    public enum ServerSaveState { Ready, Saving, SaveFailed, Stopping, Stopped, StopFailed }

    // One owner serializes all saves and shutdown. No protocol/business knowledge or I/O in state callbacks.
    public sealed class ServerSaveLifecycle
    {
        private enum Trigger { Save, Saved, SaveError, Stop, Stopped, StopError }
        private readonly object gate = new object();
        private readonly Action save;
        private readonly StateMachine<ServerSaveState, Trigger> machine;
        private int stopRequested;

        public ServerSaveLifecycle(Action save)
        {
            this.save = save ?? throw new ArgumentNullException(nameof(save));
            machine = new StateMachine<ServerSaveState, Trigger>(ServerSaveState.Ready);
            machine.Configure(ServerSaveState.Ready).Permit(Trigger.Save, ServerSaveState.Saving).Permit(Trigger.Stop, ServerSaveState.Stopping);
            machine.Configure(ServerSaveState.Saving).Permit(Trigger.Saved, ServerSaveState.Ready).Permit(Trigger.SaveError, ServerSaveState.SaveFailed);
            machine.Configure(ServerSaveState.SaveFailed).Permit(Trigger.Save, ServerSaveState.Saving).Permit(Trigger.Stop, ServerSaveState.Stopping);
            machine.Configure(ServerSaveState.Stopping).Permit(Trigger.Stopped, ServerSaveState.Stopped).Permit(Trigger.StopError, ServerSaveState.StopFailed);
        }

        public bool StopRequested => Volatile.Read(ref stopRequested) != 0;
        public ServerSaveState State { get { lock (gate) return machine.State; } }

        public bool TrySave()
        {
            // Coalesce overlapping timer ticks rather than queueing blocked ThreadPool workers.
            if (StopRequested || !Monitor.TryEnter(gate)) return false;
            try
            {
                if (StopRequested) return false;
                machine.Fire(Trigger.Save);
                try { save(); machine.Fire(Trigger.Saved); return true; }
                catch { machine.Fire(Trigger.SaveError); throw; }
            }
            finally { Monitor.Exit(gate); }
        }

        public void Stop(Action quiesce) => StopCore(quiesce, saveFinalState: true);

        public void StopWithoutSave(Action quiesce) => StopCore(quiesce, saveFinalState: false);

        private void StopCore(Action quiesce, bool saveFinalState)
        {
            ArgumentNullException.ThrowIfNull(quiesce);
            // Reject queued timer work even while a previous save is draining.
            Interlocked.Exchange(ref stopRequested, 1);
            lock (gate)
            {
                if (machine.State == ServerSaveState.Stopped) return;
                if (machine.State == ServerSaveState.StopFailed)
                    throw new InvalidOperationException("Previous shutdown did not complete.");
                machine.Fire(Trigger.Stop);
                try
                {
                    quiesce();
                    if (saveFinalState) save();
                    machine.Fire(Trigger.Stopped);
                }
                catch { machine.Fire(Trigger.StopError); throw; }
            }
        }
    }
}
