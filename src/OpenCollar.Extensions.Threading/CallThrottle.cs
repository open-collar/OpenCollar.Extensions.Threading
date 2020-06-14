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
    ///     A class providing support for methods that must not be called too often (e.g. UI updates).
    /// </summary>
    /// <remarks>
    ///     Gurantees that a gap of at least X ms will be left between calls to the wrapped method. If this is exceeded
    ///     then all but the last invocation will be ignored and the last call will be delayed until at least X ms have passed.
    /// </remarks>
    [DebuggerDisplay("CallThrottle: Name={Name}")]
    public sealed class CallThrottle : IDisposable
    {
        /// <summary>
        ///     The call that will be invoked.
        /// </summary>
        private readonly Action _call;

        /// <summary>
        ///     A lock used to control access to the <see cref="_calling" /> field.
        /// </summary>
        private readonly object _callingLock = new object();

        /// <summary>
        ///     The minimum time between calls.
        /// </summary>
        private readonly TimeSpan _minimumInterval;

        /// <summary>
        ///     The name to associate with this throttle.
        /// </summary>
        private readonly string _name;

        /// <summary>
        ///     A timer used to trigger callbacks that have been delayed.
        /// </summary>
        private readonly Timer _timer;

        /// <summary>
        ///     A lock used to control access to the timer and control fields.
        /// </summary>
        private readonly object _timerLock = new object();

        /// <summary>
        ///     A call is currently active.
        /// </summary>
        private bool _calling;

        /// <summary>
        ///     A flag indicating that we this class has been disposed of.
        /// </summary>
        private bool _disposed;

        /// <summary>
        ///     The earliest time of the next permitted call.
        /// </summary>
        private DateTime _nextPermittedCallTime;

        /// <summary>
        ///     A flag indicating that we are currently throttling a call.
        /// </summary>
        private bool _throttling;

        /// <summary>
        ///     Initializes a new instance of the <see cref="CallThrottle" /> class.
        /// </summary>
        /// <param name="call">
        ///     The call that will be throttled.
        /// </param>
        /// <param name="minimumInterval">
        ///     The minimum interval between calls.
        /// </param>
        /// <param name="name">
        ///     The name to associate with this throttle.
        /// </param>
        public CallThrottle([JetBrains.Annotations.NotNull] Action call, TimeSpan minimumInterval, string name)
        {
            call.Validate(nameof(call), ObjectIs.NotNull);

            _call = call;
            _name = name;
            _minimumInterval = minimumInterval;
            _timer = new Timer(Call, null, Timeout.Infinite, Timeout.Infinite);
            _nextPermittedCallTime = DateTime.Now + minimumInterval;
        }

        /// <summary>
        ///     Gets the minimum interval between calls.
        /// </summary>
        public TimeSpan MinimumInterval => _minimumInterval;

        /// <summary>
        ///     Gets the name associated with this throttle.
        /// </summary>
        public string Name => _name;

        /// <summary>
        ///     Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if(_disposed)
                return;

            // This should prevent any further calls.
            _disposed = true;
            _throttling = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);

            // These should also help prevent further calls.
            _calling = true;
            _nextPermittedCallTime = DateTime.MaxValue;

            // Release resources
            _timer.Dispose();
        }

        /// <summary>
        ///     Attempt to invoke the wrapped callback.
        /// </summary>
        /// <remarks>
        ///     If a call has been made
        /// </remarks>
        public void Invoke()
        {
            if(_disposed)
                throw new ObjectDisposedException(_name, "This CallThrottle has been disposed of.");

            var now = DateTime.Now;
            bool allowCallNow;
            lock(_timerLock)
            {
                // If it is OK to call now, do so.
                allowCallNow = now >= _nextPermittedCallTime;
            }

            if(allowCallNow)
            {
                OnCall();
                return;
            }

            lock(_timerLock)
            {
                // If we are already awaiting a timed call then just let this one go
                if(_throttling)
                    return;

                // If there are no calls currently scheduled then set the timer
                var span = _nextPermittedCallTime - now;
                _throttling = true;
                _timer.Change((int)span.TotalMilliseconds, (int)span.TotalMilliseconds);
            }
        }

        /// <summary>
        ///     Called when the timer fires for a delayed call.
        /// </summary>
        /// <param name="state">
        ///     The state.
        /// </param>
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification =
            "This code must not raise any exceptions.")]
        private void Call(object state)
        {
            if(_disposed)
                return;

            try
            {
                OnCall();
            }
            catch(Exception ex)
            {
                OpenCollar.Extensions.ExceptionManager.OnUnhandledException(ex);
            }
        }

        /// <summary>
        ///     Handles calling the
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification =
            "This code must not raise any exceptions.")]
        private void OnCall()
        {
            if(_disposed)
                return;

            // Somehow we have clashed with an existing call (long-running calls perhaps?) Give up for now, the timer
            // will repeat and we'll try again in X milliseconds.
            if(_calling)
                return;

            lock(_callingLock)
            {
                // Somehow we have clashed with an existing call (long-running calls perhaps?) Give up for now, the
                // timer will repeat and we'll try again in X milliseconds.
                if(_calling)
                    return;

                // Change the flag to ensure that no new calls are made if this one is long running.
                _calling = true;
            }

            // We have got through sucessfully - we will make the call.
            lock(_timerLock)
            {
                // Clear the flag, we are making the call.
                _throttling = false;

                // Prevent any calls being initiated by callbacks until we have finished.
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }

            // Actually make the call.
            try
            {
                if(_disposed)
                    return;

                _call();
            }
            catch(Exception ex)
            {
                // We treat this as an unhandled exception and let it be dealt with centrally rather than passing up the
                // callstack. In some cases this could be dealt with by the calling code (for example when the timer has
                // not need to be invoked), however to deal with errors differently depending on where this method is
                // called from would make life very complicated in the calling code.
                OpenCollar.Extensions.ExceptionManager.OnUnhandledException(ex);
            }

            if(_disposed)
                return;

            lock(_callingLock)
            {
                _calling = false;
            }

            if(_disposed)
                return;

            // We have got through sucessfully - we will make the call.
            lock(_timerLock)
            {
                // And set the time for the next call.
                _nextPermittedCallTime = DateTime.Now + _minimumInterval;

                // If there is another call queueing we will set the timer now.
                if(_throttling)
                    _timer.Change((int)_minimumInterval.TotalMilliseconds, (int)_minimumInterval.TotalMilliseconds);
            }
        }
    }
}