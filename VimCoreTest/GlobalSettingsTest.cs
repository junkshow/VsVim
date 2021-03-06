﻿using System;
using System.Linq;
using NUnit.Framework;
using Vim;
using GlobalSettings = Vim.GlobalSettings;

namespace VimCore.UnitTest
{
    [TestFixture]
    public class GlobalSettingsTest : SettingsCommonTest
    {
        protected override string ToggleSettingName { get { return GlobalSettingNames.IgnoreCaseName; } }
        protected override IVimSettings Create()
        {
            return CreateGlobal();
        }

        private IVimGlobalSettings CreateGlobal()
        {
            return new GlobalSettings();
        }

        [Test]
        public void Sanity1()
        {
            var global = CreateGlobal();
            var all = global.AllSettings;
            Assert.IsTrue(all.Any(x => x.Name == GlobalSettingNames.IgnoreCaseName));
            Assert.IsTrue(all.Any(x => x.Name == GlobalSettingNames.ShiftWidthName));
        }

        [Test]
        public void SetByAbbreviation1()
        {
            var global = CreateGlobal();
            Assert.IsTrue(global.TrySetValueFromString("sw", "2"));
            Assert.AreEqual(2, global.ShiftWidth);
        }

        [Test]
        public void SetByAbbreviation2()
        {
            var global = CreateGlobal();
            Assert.IsFalse(global.IgnoreCase);
            Assert.IsTrue(global.TrySetValueFromString("ic", "true"));
            Assert.IsTrue(global.IgnoreCase);
        }

        [Test]
        public void IsVirtualEditOneMore1()
        {
            var global = CreateGlobal();
            global.VirtualEdit = String.Empty;
            Assert.IsFalse(global.IsVirtualEditOneMore);
        }

        [Test]
        public void IsVirtualEditOneMore2()
        {
            var global = CreateGlobal();
            global.VirtualEdit = "onemore";
            Assert.IsTrue(global.IsVirtualEditOneMore);
        }

        [Test]
        public void IsVirtualEditOneMore3()
        {
            var global = CreateGlobal();
            global.VirtualEdit = "onemore,blah";
            Assert.IsTrue(global.IsVirtualEditOneMore);
        }

        /// <summary>
        /// Setting a setting should raise the event even if the values are the same.  This is 
        /// depended on by the :noh feature
        /// </summary>
        [Test]
        public void SetShouldRaise()
        {
            var global = CreateGlobal();
            var seen = false;
            global.HighlightSearch = true;
            global.SettingChanged += delegate { seen = true; };
            global.HighlightSearch = true;
            Assert.IsTrue(seen);
        }

    }
}
