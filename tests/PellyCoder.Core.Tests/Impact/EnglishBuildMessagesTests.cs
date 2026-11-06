using System.Globalization;
using PellyCoder.Core.Impact;

namespace PellyCoder.Core.Tests.Impact;

[Collection("Culture")]
public class EnglishBuildMessagesTests
{
    [Fact]
    public void Pin_fixes_the_default_ui_culture_for_other_threads_and_restores_it()
    {
        var korean = CultureInfo.GetCultureInfo("ko-KR");
        var before = CultureInfo.DefaultThreadCurrentUICulture;
        var current = CultureInfo.CurrentUICulture;
        CultureInfo.DefaultThreadCurrentUICulture = korean;
        try
        {
            using (EnglishBuildMessages.Pin())
            {
                CultureInfo? seen = null;
                // A thread outside the pinned flow (the build host's callbacks) takes the default culture.
                using (ExecutionContext.SuppressFlow())
                {
                    var t = new Thread(() => seen = CultureInfo.CurrentUICulture);
                    t.Start();
                    t.Join();
                }

                Assert.Equal(CultureInfo.InvariantCulture.Name, seen!.Name);
            }

            Assert.Equal(korean, CultureInfo.DefaultThreadCurrentUICulture);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = before;
            CultureInfo.CurrentUICulture = current;
        }
    }
}
