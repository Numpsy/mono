using System;
using System.Web.UI;
using MonoTests.SystemWeb.Framework;

public partial class MyPage : System.Web.UI.Page
{
	//FIXME: mono defines its own constructor here
#if BUG_78521_FIXED || !MONO_ONLY
	public MyPage ()
#else
	protected override void OnPreInit (EventArgs e)
#endif
	{
		WebTest t = WebTest.CurrentTest;
		if (t != null)
			t.Invoke (this);
	}
		
	public override void VerifyRenderingInServerForm (Control c)
	{

	}
}
