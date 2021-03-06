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

using Xunit;
using Sensus.Probes.User.Scripts;
using Sensus.UI.Inputs;
using System.Linq;

namespace Sensus.Tests.Probes.User.Scripts
{
    
    public class ScriptTests
    {
        [Fact]
        public void ScriptCopySameIdTest()
        {
            ScriptProbe probe = new ScriptProbe();
            ScriptRunner runner = new ScriptRunner("test", probe);
            Script script = new Script(runner);
            InputGroup group = new InputGroup();
            Input input1 = new SliderInput();
            Input input2 = new SliderInput();
            group.Inputs.Add(input1);
            group.Inputs.Add(input2);
            script.InputGroups.Add(group);

            Script copy = script.Copy(false);

            Assert.Same(script.Runner, copy.Runner);
            Assert.Equal(script.Id, copy.Id);
            Assert.Equal(script.InputGroups.Single().Id, copy.InputGroups.Single().Id);
            Assert.Equal(script.InputGroups.Single().Inputs.First().Id, copy.InputGroups.Single().Inputs.First().Id);
            Assert.Equal(script.InputGroups.Count, copy.InputGroups.Count);
            Assert.Equal(script.InputGroups.Single().Inputs.Count, copy.InputGroups.Single().Inputs.Count);
        }

        [Fact]
        public void ScriptCopyNewIdTest()
        {
            ScriptProbe probe = new ScriptProbe();
            ScriptRunner runner = new ScriptRunner("test", probe);
            Script script = new Script(runner);
            InputGroup group = new InputGroup();
            Input input1 = new SliderInput();
            Input input2 = new SliderInput();
            group.Inputs.Add(input1);
            group.Inputs.Add(input2);
            script.InputGroups.Add(group);

            Script copy = script.Copy(true);

            Assert.Same(script.Runner, copy.Runner);
            Assert.NotEqual(script.Id, copy.Id);
            Assert.Equal(script.InputGroups.Single().Id, copy.InputGroups.Single().Id);
            Assert.Equal(script.InputGroups.Single().Inputs.First().Id, copy.InputGroups.Single().Inputs.First().Id);    
            Assert.Equal(script.InputGroups.Count, copy.InputGroups.Count);
            Assert.Equal(script.InputGroups.Single().Inputs.Count, copy.InputGroups.Single().Inputs.Count);
        }
    }
}
