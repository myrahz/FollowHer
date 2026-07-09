using FollowHer.Core.Combat.State;
using System;

namespace FollowHer.Core.Combat
{
    public interface IRoutine : IDisposable
    {
        string Name { get; }
        bool CanExecute { get; }
        RoutineState State { get; }
        bool Initialize();
        void Stop();
    }
}