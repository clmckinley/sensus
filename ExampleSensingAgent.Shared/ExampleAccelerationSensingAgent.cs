﻿// Copyright 2014 The Rector & Visitors of the University of Virginia
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

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Sensus;
using Sensus.Probes.Movement;
using System.Linq;
using Sensus.Probes.Location;
using Sensus.Probes;
using System.Threading;
using Sensus.Extensions;

namespace ExampleSensingAgent
{
    /// <summary>
    /// Example acceleration sensing agent. Demonstrates concepts related to control criterion checking as well as 
    /// sensing control, in particular temporary device keep-awake and increased sampling rates.
    /// </summary>
    public class ExampleAccelerationSensingAgent : SensingAgent
    {
        private double _averageLinearMagnitudeThreshold;
        private double? _controlAccelerometerMaxDataStoresPerSecond;
        private double? _idleAccelerometerMaxDataStoresPerSecond;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:ExampleSensingAgent.ExampleAccelerationSensingAgent"/> class. As noted in
        /// the [adaptive sensing](xref:adaptive_sensing) article, this class provides the parameterless constructor required for
        /// run time initialization of the agent.
        /// </summary>
        public ExampleAccelerationSensingAgent()
            : base("Acceleration", "ALM / Proximity", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5))
        {
            _averageLinearMagnitudeThreshold = 0.1;
            _controlAccelerometerMaxDataStoresPerSecond = 60;
        }

        public override async Task SetPolicyAsync(JObject policy)
        {
            await base.SetPolicyAsync(policy);

            _averageLinearMagnitudeThreshold = double.Parse(policy.GetValue("alm-threshold").ToString());
            _idleAccelerometerMaxDataStoresPerSecond = double.Parse(policy.GetValue("control-acc-rate").ToString());
        }

        protected override void UpdateObservedData(Dictionary<Type, List<IDatum>> typeData)
        {
            foreach (Type type in typeData.Keys)
            {
                List<IDatum> data = typeData[type];

                // trim collections by size
                while (data.Count > 100)
                {
                    data.RemoveAt(0);
                }
            }
        }

        protected override bool ObservedDataMeetControlCriterion(Dictionary<Type, List<IDatum>> typeData, Type observedDatumType)
        {
            bool criterionMet = false;

            if (observedDatumType == null)
            {
                return NearSurface() || AccelerationAverageLinearMagnitudeExceeds(_averageLinearMagnitudeThreshold);
            }
            else if (observedDatumType.ImplementsInterface<IProximityDatum>())
            {
                return NearSurface();
            }
            else if (observedDatumType.ImplementsInterface<IAccelerometerDatum>())
            {
                return AccelerationAverageLinearMagnitudeExceeds(_averageLinearMagnitudeThreshold);
            }

            return criterionMet;
        }

        protected override async Task OnOpportunisticControlAsync(CancellationToken cancellationToken)
        {
            await OnControlAsync(cancellationToken);
        }

        protected override async Task OnActiveControlAsync(CancellationToken cancellationToken)
        {
            await OnControlAsync(cancellationToken);
        }

        private async Task OnControlAsync(CancellationToken cancellationToken)
        {
            await SensusServiceHelper.KeepDeviceAwakeAsync();

            // increase sampling rate
            if (Protocol.TryGetProbe<IAccelerometerDatum, IListeningProbe>(out IListeningProbe accelerometerProbe))
            {
                _idleAccelerometerMaxDataStoresPerSecond = accelerometerProbe.MaxDataStoresPerSecond;
                accelerometerProbe.MaxDataStoresPerSecond = _controlAccelerometerMaxDataStoresPerSecond;

                await accelerometerProbe.RestartAsync();
            }
        }

        protected override async Task OnEndingControlAsync(CancellationToken cancellationToken)
        {
            // revert sampling rate
            if (Protocol.TryGetProbe<IAccelerometerDatum, IListeningProbe>(out IListeningProbe accelerometerProbe))
            {
                accelerometerProbe.MaxDataStoresPerSecond = _idleAccelerometerMaxDataStoresPerSecond;

                await accelerometerProbe.RestartAsync();
            }

            await SensusServiceHelper.LetDeviceSleepAsync();
        }
    }
}