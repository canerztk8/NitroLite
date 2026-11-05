using NitroLite.Core;
using Xunit;
namespace NitroLite.Tests;
public class RepairRegressionTests
{
 [Fact] public async Task QueuedReconnectIsNotDropped()
 {
  using var a=new MockAcerAgentClient(); var c=new CpuPowerService(false,true); using var gate=new SemaphoreSlim(1,1);
  using var p=new PowerAutomationService(a,c,new TestDisplayRefreshRateServiceStub(),new AppSettings(),false,gate);
  await p.ProcessPowerTransitionAsync(true); await gate.WaitAsync();
  var dc=p.ProcessPowerTransitionAsync(false); var ac=p.ProcessPowerTransitionAsync(true);
  gate.Release(); await Task.WhenAll(dc,ac); Assert.True(p.LastAcStatus);
 }
 [Fact] public void ExactBoostPreservesEnabledValue()
 {
  var c=new CpuPowerService(false,true){MockPowerCfgQueryOutput="Current AC Power Setting Index: 0x00000001"};
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
  Assert.Contains("Enable-ScheduledTask",script); Assert.DoesNotContain("Restore aborted",script);
 }
}
