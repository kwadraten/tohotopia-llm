using System;

namespace TohotopiaMod;

// Rendering/input may use the native spectator branch; the authoritative
// simulation must never be registered as a spectator replica.
internal sealed class SpectatorAccess
{
    int simulation, presentation, chat, settlement;
    internal bool Enabled { get; set; }
    internal bool CanExecute => !Enabled || simulation > 0;
    internal bool Presentation => Enabled && presentation > 0;
    internal bool ChatServer => Enabled && chat > 0;
    internal bool EvaluatingSettlement => Enabled && settlement > 0;
    internal IDisposable Settle() { settlement++; return new Scope(() => settlement--); }
    internal IDisposable Simulation() { simulation++; return new Scope(() => simulation--); }
    internal IDisposable Present() { presentation++; return new Scope(() => presentation--); }
    internal IDisposable ServeChat() { chat++; return new Scope(() => chat--); }
    sealed class Scope(Action leave) : IDisposable
    {
        Action? release = leave;
        public void Dispose() { var action = release; release = null; action?.Invoke(); }
    }
}
