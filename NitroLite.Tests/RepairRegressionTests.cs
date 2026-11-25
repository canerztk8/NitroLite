using NitroLite.Core;
using Xunit;
namespace NitroLite.Tests;
public class RepairRegressionTests
{
 [Fact] public void ExactBoostPreservesEnabledValue()
 {
  // Real powercfg output always carries the setting GUID; the parser is GUID-scoped so it does not depend on the
  // display language of the labels around it.
  var c=new CpuPowerService(false,true){MockPowerCfgQueryOutput=
   "    Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Processor performance boost mode)\n"+
   "      Current AC Power Setting Index: 0x00000001\n"+
   "      Current DC Power Setting Index: 0x00000001\n"};
  Assert.Equal(1,(int?)c.QueryBoostModeExact(true));
 }
 [Fact] public void BuiltinPresetCannotBeSilentlyOverwritten()
 {
  var s=new RgbProfileService(); var name=s.GetPresets().First().Name;
  Assert.Throws<ArgumentException>(()=>s.SaveCustomPreset(name,"STATIC",1,1,1,"#ffffff",new()));
 }
 [Fact] public void TaskOnlyRestoreGeneratesTaskCommand()
 {
  var s=new DebloatService(); var script=s.GenerateRestoreScript(new(){["Task:NitroSenseLauncher"]="Ready"});
  Assert.Contains("Enable-ScheduledTask",script); Assert.DoesNotContain(DebloatService.NoBackupMessage,script);
 }
}
