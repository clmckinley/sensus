﻿#region copyright
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
#endregion

using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SensusService.Probes.User
{
    public class Script
    {
        #region static members
        private static JsonSerializerSettings _jsonSerializerSettings = new JsonSerializerSettings
        {
            PreserveReferencesHandling = PreserveReferencesHandling.Objects,
            ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
            ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor
        };

        public static Script FromJSON(string json)
        {
            return JsonConvert.DeserializeObject<Script>(json, _jsonSerializerSettings);
        }
        #endregion

        private string _name;
        private List<Prompt> _prompts;
        private bool _running;

        public string Name
        {
            get { return _name; }
            set { _name = value; }
        }

        public List<Prompt> Prompts
        {
            get { return _prompts; }
            set { _prompts = value; }
        }

        private Script()
        {
            _running = false;
        }

        public Script(string name, params Prompt[] prompts)
            : this()
        {
            _name = name;

            if (prompts != null)
                _prompts = prompts.ToList();
        }

        public void Save(string path)
        {
            using (StreamWriter file = new StreamWriter(path))
            {
                file.Write(JsonConvert.SerializeObject(this, _jsonSerializerSettings));
                file.Close();
            }
        }

        public Task<List<ScriptDatum>> RunAsync(Datum previous, Datum current)
        {
            return Task.Run<List<ScriptDatum>>(async () =>
                {
                    List<ScriptDatum> data = new List<ScriptDatum>();

                    lock (this)
                    {
                        if (_running)
                            return data;
                        else
                            _running = true;
                    }

                    if (_prompts != null)
                        foreach (Prompt prompt in _prompts)
                        {
                            ScriptDatum datum = await prompt.RunAsync();
                            if (datum != null)
                                data.Add(datum);
                        }

                    lock (this)
                        _running = false;

                    return data;
                });
        }
    }
}
