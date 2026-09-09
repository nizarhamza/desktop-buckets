using System.Linq;
using System.Xml.Linq;
using DesktopBuckets.Services;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class StartupRegistrationTests
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        [Fact]
        public void BuildTaskXml_is_well_formed_and_carries_command_marker_trigger_and_sid()
        {
            const string exe = @"C:\Users\sample\AppData\Local\Programs\Desktop Buckets\DesktopBuckets.exe";
            const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

            var doc = XDocument.Parse(StartupRegistration.BuildTaskXml(exe, sid)); // throws if malformed

            Assert.Equal(exe, (string?)doc.Descendants(Ns + "Command").Single());
            Assert.Equal("--autostart", (string?)doc.Descendants(Ns + "Arguments").Single());

            var trigger = doc.Descendants(Ns + "LogonTrigger").Single();
            Assert.Equal(sid, (string?)trigger.Element(Ns + "UserId"));
            Assert.Equal("PT20S", (string?)trigger.Element(Ns + "Delay"));

            Assert.Equal(sid, (string?)doc.Descendants(Ns + "Principal").Single().Element(Ns + "UserId"));
        }

        [Fact]
        public void BuildTaskXml_does_not_gate_on_battery_and_has_no_run_time_limit()
        {
            var doc = XDocument.Parse(StartupRegistration.BuildTaskXml(@"C:\x\DesktopBuckets.exe", "S-1-5-18"));

            Assert.Equal("false", (string?)doc.Descendants(Ns + "DisallowStartIfOnBatteries").Single());
            Assert.Equal("false", (string?)doc.Descendants(Ns + "StopIfGoingOnBatteries").Single());
            Assert.Equal("PT0S", (string?)doc.Descendants(Ns + "ExecutionTimeLimit").Single());
            Assert.Equal("LeastPrivilege", (string?)doc.Descendants(Ns + "RunLevel").Single());
        }

        [Fact]
        public void BuildTaskXml_xml_escapes_the_exe_path()
        {
            const string weird = @"C:\a & b\<test>\DesktopBuckets.exe";
            var xml = StartupRegistration.BuildTaskXml(weird, "S-1-5-18");

            Assert.DoesNotContain("a & b", xml);               // the raw ampersand must not survive
            var doc = XDocument.Parse(xml);                    // and it must still parse
            Assert.Equal(weird, (string?)doc.Descendants(Ns + "Command").Single());
        }
    }
}
