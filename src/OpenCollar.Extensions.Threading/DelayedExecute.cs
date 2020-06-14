/*
 * This file is part of OpenCollar.Extensions.Collections.
 *
 * OpenCollar.Extensions.Collections is free software: you can redistribute it
 * and/or modify it under the terms of the GNU General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or (at your
 * option) any later version.
 *
 * OpenCollar.Extensions.Collections is distributed in the hope that it will be
 * useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public
 * License for more details.
 *
 * You should have received a copy of the GNU General Public License along with
 * OpenCollar.Extensions.Collections.  If not, see <https://www.gnu.org/licenses/>.
 *
 * Copyright © 2019-2020 Jonathan Evans (jevans@open-collar.org.uk).
 */

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

using OpenCollar.Extensions.Validation;

namespace OpenCollar.Extensions.Threading
{
    /// <summary>
    ///     A class that allows a call to an action to be postponed until the execute method has not been called for a
    ///     minimum period.
    /// </summary>
    [DebuggerDisplay("DelayedExecute: {Name} ({MinimumWait})")]
    public class DelayedExecute : OpenCollar.Extensions.Disposable
    {
        /// <summary>
        ///     The action to execute.
        /// </summary>
        [JetBrains.Annotations.NotNull]
        private readonly Action _action;

        /// <summary>
        ///     The lock used to control concurrent access to the timer.
        /// </summary>
        [JetBrains.Annotations.NotNull]
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        /// <summary>
        ///     The minimum period that must pass between the last call to <see cref="Execute" /> and the action being executed.
        /// </summary>
        private readonly TimeSpan _minimumWait;

        /// <summary>
        ///     The name to display in exceptions and when debugging.
        /// </summary>
        private readonly string _name;

        /// <summary>
        ///     The timer that fires the action after an appropriate wait.
        /// </summary>
        [JetBrains.Annotations.NotNull]
        private readonly Timer _timer;

        /// <summary>
        ///     A flag that indicates that a call to execute is pending.
        /// </summary>
        private volatile bool _executeWaiting;

        /// <summary>
        ///     A flag that indicates execution is active.
        /// </summary>
        private volatile bool _executing;

#if DEBUG

        /// <summary>
        ///     A flag used to keep track of whether the timer has been set.
        /// </summary>
        private bool _timerSet;

#endif

        /// <summary>
        ///     Initializes a new instance of the <see cref="DelayedExecute" /> class.
        /// </summary>
        /// <param name="action">
        ///     The action to execute.
        /// </param>
        /// <param name="minimumWait">
        ///     The minimum wait before executing the action.
        /// </param>
        /// <param name="name">
        ///     The name to display in exceptions and when debugging.
        /// </param>
        /// <exception cref="System.ArgumentOutOfRangeException">
        ///     minimumWait;The 'minimumWait' argument must be greater than zero.
        /// </exception>
        public DelayedExecute([JetBrains.Annotations.NotNull] Action action, TimeSpan minimumWait, [JetBrains.Annotations.NotNull] string name)
        {
            name.Validate(nameof(name), StringIs.NotNullEmptyOrWhiteSpace);
            action.Validate(nameof(action), ObjectIs.NotNull);
            if(minimumWait.TotalMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(minimumWait), minimumWait,
                                                      Resources.Exceptions.DelayedExecute_MinimumWaitMustBeGreaterThanZero);

            _action = action;
            _minimumWait = minimumWait;
            _name = name;
            _timer = new Timer(OnExecute, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        ///     The minimum period that must pass between the last call to <see cref="Execute" /> and the action being executed.
        /// </summary>
        public TimeSpan MinimumWait => _minimumWait;

        /// <summary>
        ///     The name to display in exceptions and when debugging.
        /// </summary>
        public string Name => _name;

        /// <summary>
        ///     Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing">
        ///     <see langword="true" /> to release both managed and unmanaged resources; <see langword="false" /> to
        ///     release only unmanaged resources.
        /// </param>
        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                _lock.EnterWriteLock();
                try
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    _timer.Dispose();
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
                _lock.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        ///     Attempts to execute the delayed action.
        /// </summary>
        public void Execute()
        {
            if(IsDisposed)
                return;

            if(_executing)
            {
                _executeWaiting = true;
                return;
            }

            _lock.EnterWriteLock();
            try
            {
#if DEBUG
                if(!_timerSet)
                    _timerSet = true;
#endif
                if(_executing)
                {
                    _executeWaiting = true;
                    return;
                }

                _timer.Change((int)_minimumWait.TotalMilliseconds, Timeout.Infinite);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        ///     Called when the minimum period of time since the last attempt to execute has passed.
        /// </summary>
        /// <param name="unused">
        ///     Not used.
        /// </param>
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification =
            "This code must not raise any exceptions.")]
        private void OnExecute(object unused)
        {
            if(IsDisposed)
                return;

            try
            {
                _lock.EnterWriteLock();
                try
                {
                    _executing = true;
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
#if DEBUG
                    _timerSet = false;
#endif
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                // Abort
                if(IsDisposed)
                {
                    _executeWaiting = false;
                    _executing = false;
                    return;
                }
                try
                {
                    // Execute Debug.WriteLine("Executing: " + _name);
                    _action.Invoke();

                    // Debug.WriteLine("Executed: " + _name);

                    // Abort
                    if(IsDisposed)
                    {
                        _executeWaiting = false;
                        _executing = false;
                    }
                }
                finally
                {
                    if(!IsDisposed)
                    {
                        _lock.EnterWriteLock();
                        try
                        {
                            _executing = false;

                            if(_executeWaiting)
                            {
                                _timer.Change((int)_minimumWait.TotalMilliseconds, Timeout.Infinite);
                                _executeWaiting = false;
                            }
                        }
                        finally
                        {
                            _lock.ExitWriteLock();
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                ex.Data.Add("DelayedExecute.Name", _name);
                OpenCollar.Extensions.ExceptionManager.OnUnhandledException(ex);
            }
        }
    }
}