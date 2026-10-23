using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Steamy.Controls;

namespace Steamy.UiTests;

public sealed class ToolsSelectionTests
{
    [Fact]
    public void ToolsKeepsItsDirectSelectionWithLocalGlassAndWithoutAWorkspaceDimmer()
    {
        var document=Source("src/Steamy/MainWindow.xaml");
        var layer=Named(document,"ToolsMenuLayer");
        Assert.Single(layer.Elements());
        var dropdown=Named(document,"ToolsDropdown");
        Assert.Same(dropdown,layer.Elements().Single());
        Assert.DoesNotContain(layer.Descendants(),element=>element.Name.LocalName is "BlurEffect" or "Frame");
        var backdrop=dropdown.Descendants().Single(element=>element.Name.LocalName=="ShellBackdropView");
        Assert.Equal("True",(string?)backdrop.Attribute("IsGlass"));
        Assert.Equal("12",(string?)backdrop.Attribute("FrostRadius"));
        Assert.Equal("True",(string?)backdrop.Attribute("SamplePageSurface"));
        Assert.Equal("560",(string?)dropdown.Attribute("Width"));
        Assert.Equal("24",(string?)dropdown.Attribute("CornerRadius"));
        Assert.Equal("150",dropdown.Attributes().Single(attribute=>attribute.Name.LocalName=="SurfaceMotion.Duration").Value);
        Assert.Equal("1",dropdown.Attributes().Single(attribute=>attribute.Name.LocalName=="SurfaceMotion.StartScale").Value);
        var tools=Named(document,"ToolsMenuSection").Descendants().Where(element=>element.Name.LocalName=="Button").ToArray();
        Assert.Equal(8,tools.Length);
        Assert.All(tools,button=>Assert.Equal("ShellRoute_Click",(string?)button.Attribute("Click")));
        Assert.Contains(tools,button=>(string?)button.Attribute("Tag")=="{x:Type pages:UcOnlinePage}");
        Assert.Equal("ShellTools_Click",(string?)Named(document,"ShellToolsButton").Attribute("Click"));
    }
    [Fact]
    public void LibraryCollapseUsesLeadingSpaceAndATransparentPinnedFilter()
    {
        var document=Source("src/Steamy/Pages/LibraryPage.xaml");
        var content=Named(document,"MainContentGrid");
        Assert.DoesNotContain(content.Elements(),element=>element.Name.LocalName=="Grid.RowDefinitions");
        Assert.Equal("0",(string?)Named(document,"LibraryHeading").Attribute("Grid.Row"));
        Assert.Equal("0,68,0,0",(string?)Named(document,"LibraryGallery").Attribute("Margin"));
        Assert.Equal("Transparent",(string?)Named(document,"LibraryFilterBar").Attribute("Background"));
        Assert.Equal("64",(string?)document.Descendants().Single(element=>element.Name.LocalName=="VirtualizingGamePanel").Attribute("LeadingHeight"));
    }
    [Fact]
    public void UcOnlineDefersItsAdvancedFieldsUntilTheSettingsSectionIsOpened()
    {
        var document=Source("src/Steamy/Pages/UcOnlinePage.xaml");
        var expander=Named(document,"SettingsExpander");
        Assert.Equal("Settings_Expanded",(string?)expander.Attribute("Expanded"));
        Assert.NotEqual("True",(string?)expander.Attribute("IsExpanded"));
        Assert.Empty(Named(document,"OptionsFields").Elements());
    }
    [Fact]
    public void ShortTranslationCanBeConfiguredWithoutChangingTheDefaultEntrance()
    {
        OnSta(()=>
        {
            var control=new Border();
            Assert.Equal(320,SurfaceMotion.GetDuration(control));
            Assert.Equal(1,SurfaceMotion.GetStartScale(control));
            SurfaceMotion.SetDuration(control,150); SurfaceMotion.SetStartScale(control,1); SurfaceMotion.SetLift(control,4);
            Assert.Equal(150,SurfaceMotion.GetDuration(control)); Assert.Equal(1,SurfaceMotion.GetStartScale(control)); Assert.Equal(4,SurfaceMotion.GetLift(control));
            SurfaceMotion.SetRevealOnShow(control,true); SurfaceMotion.Reveal(control);
            SurfaceMotion.SetRevealOnShow(control,false);
            Assert.False(control.HasAnimatedProperties);
        });
    }
    private static XElement Named(XDocument document,string name)=>document.Descendants().Single(element=>(string?)element.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml")+"Name")==name);
    private static XDocument Source(string path)
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,path))) root=root.Parent;
        Assert.NotNull(root); return XDocument.Load(Path.Combine(root!.FullName,path));
    }
    private static void OnSta(Action check)
    {
        Exception? failure=null;
        var thread=new Thread(()=>{try{check();}catch(Exception error){failure=error;}}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if(failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
