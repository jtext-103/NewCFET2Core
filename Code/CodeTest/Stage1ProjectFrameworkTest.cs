using System;
using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test
{
    [TestClass]
    public class Stage1ProjectFrameworkTest
    {
        [TestMethod]
        public void CoreProjectShouldRemainNetStandard20()
        {
            GetTargetFramework("CFET2Core", "CFET2Core.csproj").Should().Be("netstandard2.0");
        }

        [TestMethod]
        public void TestProjectShouldTargetNet10()
        {
            GetTargetFramework("CodeTest", "CodeTest.csproj").Should().Be("net10.0");
        }

        private static string GetTargetFramework(string projectDirectory, string projectFile)
        {
            var projectPath = Path.Combine(GetCodeRoot(), projectDirectory, projectFile);
            var document = XDocument.Load(projectPath);

            return document.Root
                .Element("PropertyGroup")
                .Element("TargetFramework")
                .Value;
        }

        private static string GetCodeRoot()
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        }
    }
}
