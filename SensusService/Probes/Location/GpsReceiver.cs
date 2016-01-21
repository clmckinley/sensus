// Copyright 2014 The Rector & Visitors of the University of Virginia
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using SensusService.Exceptions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Plugin.Geolocator.Abstractions;
using Plugin.Geolocator;
using System.Collections.Generic;
using System.Linq;

namespace SensusService.Probes.Location
{
    /// <summary>
    /// A GPS receiver. Implemented as a singleton.
    /// </summary>
    public class GpsReceiver
    {
        #region static members

        public static readonly GpsReceiver SINGLETON = new GpsReceiver();

        public static GpsReceiver Get()
        {
            return SINGLETON;
        }

        #endregion

        private event EventHandler<PositionEventArgs> PositionChanged;

        private IGeolocator _locator;
        private bool _readingIsComing;
        private ManualResetEvent _readingWait;
        private Position _reading;
        private int _readingTimeoutMS;
        private int _minimumTimeHintMS;
        private List<Tuple<EventHandler<PositionEventArgs>, bool>> _listenerHeadings;

        private readonly object _locker = new object();

        public IGeolocator Locator
        {
            get { return _locator; }
        }

        private bool ListeningForChanges
        {      
            get { return PositionChanged != null; }        
        }

        public int MinimumDistanceThreshold
        {
            // because GPS is only so accurate, successive readings can fire the trigger even if one is not moving -- if the threshold is too small. theoretically
            // the minimum value of the threshold should be equal to the desired accuracy; however, the desired accuracy is just a request. use 2x the desired
            // accuracy just to be sure.
            get { return (int)(2 * _locator.DesiredAccuracy); }
        }

        private GpsReceiver()
        {
            _readingIsComing = false;
            _readingWait = new ManualResetEvent(false);
            _reading = null;
            _readingTimeoutMS = 120000;
            _minimumTimeHintMS = 5000;
            _locator = CrossGeolocator.Current;
            _locator.AllowsBackgroundUpdates = true;
            _locator.PausesLocationUpdatesAutomatically = false;
            _locator.DesiredAccuracy = 50;  // setting this too low appears to result in very delayed GPS fixes.
            _listenerHeadings = new List<Tuple<EventHandler<PositionEventArgs>, bool>>();

            _locator.PositionChanged += (o, e) =>
            {
                SensusServiceHelper.Get().Logger.Log("GPS position has changed.", LoggingLevel.Verbose, GetType());

                if (PositionChanged != null)
                    PositionChanged(o, e);
            };
        }

        public async void AddListener(EventHandler<PositionEventArgs> listener, bool includeHeading)
        {      
            if (ListeningForChanges)
                await _locator.StopListeningAsync();      
                      
            PositionChanged += listener;       

            _listenerHeadings.Add(new Tuple<EventHandler<PositionEventArgs>, bool>(listener, includeHeading));

            await _locator.StartListeningAsync(_minimumTimeHintMS, _locator.DesiredAccuracy, _listenerHeadings.Any(t => t.Item2));

            SensusServiceHelper.Get().Logger.Log("GPS receiver is now listening for changes.", LoggingLevel.Normal, GetType());        
        }

        public async void RemoveListener(EventHandler<PositionEventArgs> listener)
        {      
            if (ListeningForChanges)
                await _locator.StopListeningAsync();      
                       
            PositionChanged -= listener;  

            _listenerHeadings.RemoveAll(t => t.Item1 == listener);
                       
            if (ListeningForChanges)
                await _locator.StartListeningAsync(_minimumTimeHintMS, _locator.DesiredAccuracy, _listenerHeadings.Any(t => t.Item2));
            else
                SensusServiceHelper.Get().Logger.Log("All listeners removed from GPS receiver. Stopped listening.", LoggingLevel.Normal, GetType());       
        }

        /// <summary>
        /// Gets a GPS reading, reusing an old one if it isn't too old. Will block the current thread while waiting for a GPS reading. Should not
        /// be called from the main / UI thread, since GPS runs on main thread (will deadlock).
        /// </summary>
        /// <returns>The reading.</returns>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="callback">Callback to run when reading is obtained</param>
        public void GetReadingAsync(CancellationToken cancellationToken, Action<Position> callback)
        {
            new Thread(() =>
                {
                    callback(GetReading(cancellationToken));

                }).Start();
        }

        /// <summary>
        /// Gets a GPS reading, reusing an old one if it isn't too old. Will block the current thread while waiting for a GPS reading. Should not
        /// be called from the main / UI thread, since GPS runs on main thread (will deadlock).
        /// </summary>
        /// <returns>The reading.</returns>
        /// <param name="cancellationToken">Cancellation token.</param>
        public Position GetReading(CancellationToken cancellationToken)
        {
            return GetReading(0, cancellationToken);
        }

        /// <summary>
        /// Gets a GPS reading, reusing an old one if it isn't too old. Will block the current thread while waiting for a GPS reading. Should not
        /// be called from the main / UI thread, since GPS runs on main thread (will deadlock).
        /// </summary>
        /// <returns>The reading.</returns>
        /// <param name="maxReadingAgeForReuseMS">Maximum age of old reading to reuse (milliseconds).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="callback">Callback to run when reading is obtained</param>
        public void GetReadingAsync(int maxReadingAgeForReuseMS, CancellationToken cancellationToken, Action<Position> callback)
        {  
            new Thread(() =>
                {
                    callback(GetReading(maxReadingAgeForReuseMS, cancellationToken));

                }).Start();
        }

        /// <summary>
        /// Gets a GPS reading, reusing an old one if it isn't too old. Will block the current thread while waiting for a GPS reading. Should not
        /// be called from the main / UI thread, since GPS runs on main thread (will deadlock).
        /// </summary>
        /// <returns>The reading.</returns>
        /// <param name="maxReadingAgeForReuseMS">Maximum age of old reading to reuse (milliseconds).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public Position GetReading(int maxReadingAgeForReuseMS, CancellationToken cancellationToken)
        {  
            lock (_locker)
            {
                // reuse existing reading if it isn't too old
                if (_reading != null && maxReadingAgeForReuseMS > 0)
                {
                    double readingAgeMS = (DateTimeOffset.UtcNow - _reading.Timestamp).TotalMilliseconds;
                    if (readingAgeMS <= maxReadingAgeForReuseMS)
                    {
                        SensusServiceHelper.Get().Logger.Log("Reusing previous GPS reading, which is " + readingAgeMS + "ms old (maximum = " + maxReadingAgeForReuseMS + "ms).", LoggingLevel.Verbose, GetType());
                        return _reading;
                    }
                }

                if (_readingIsComing)
                    SensusServiceHelper.Get().Logger.Log("A GPS reading is coming. Will wait for it.", LoggingLevel.Debug, GetType());
                else
                {
                    _readingIsComing = true;  // tell any subsequent, concurrent callers that we're taking a reading
                    _readingWait.Reset();  // make them wait

                    new Thread(async () =>
                        {
                            try
                            {
                                SensusServiceHelper.Get().Logger.Log("Taking GPS reading.", LoggingLevel.Debug, GetType());

                                DateTimeOffset readingStart = DateTimeOffset.UtcNow;
                                Position newReading = await _locator.GetPositionAsync(timeoutMilliseconds: _readingTimeoutMS, token: cancellationToken);
                                DateTimeOffset readingEnd = DateTimeOffset.UtcNow;

                                if (newReading != null)
                                {                                   
                                    // create copy of new position to keep return references separate, since the same Position object is returned multiple times when a change listener is attached.
                                    _reading = new Position(newReading);                                   

                                    SensusServiceHelper.Get().Logger.Log("GPS reading obtained in " + (readingEnd - readingStart).TotalSeconds + " seconds.", LoggingLevel.Verbose, GetType());
                                }
                            }
                            catch (Exception ex)
                            {
                                SensusServiceHelper.Get().Logger.Log("GPS reading failed:  " + ex.Message, LoggingLevel.Normal, GetType());
                                _reading = null;
                            }

                            _readingWait.Set();  // tell anyone waiting on the shared reading that it is ready
                            _readingIsComing = false;  // direct any future calls to this method to get their own reading

                        }).Start();
                }
            }

            _readingWait.WaitOne(_readingTimeoutMS);

            if (_reading == null)
                SensusServiceHelper.Get().Logger.Log("GPS reading is null.", LoggingLevel.Normal, GetType());

            return _reading;
        }
    }
}