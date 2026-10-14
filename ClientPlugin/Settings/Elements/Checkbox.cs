using Sandbox.Graphics.GUI;
using System;
using System.Collections.Generic;

namespace ClientPlugin.Settings.Elements;

class CheckboxAttribute : Attribute, IElement
{
    public readonly string Label;
    public readonly string Description;

    public CheckboxAttribute(string label = null, string description = null)
    {
        Label = label;
        Description = description;
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {               
        var label = Tools.Tools.GetLabelOrDefault(name, Label);
        bool refreshing = false;
        var checkbox = new MyGuiControlCheckbox(toolTip: Description) { IsChecked = (bool)propertyGetter() };
        checkbox.IsCheckedChanged = x => { if (!refreshing) propertySetter(x.IsChecked); };
        return new List<Control>()
        {
            new Control(new MyGuiControlLabel(text: label), minWidth: Control.LabelMinWidth),
            new Control(checkbox)
            {
                RefreshValue = () =>
                {
                    refreshing = true;
                    try { checkbox.IsChecked = (bool)propertyGetter(); }
                    finally { refreshing = false; }
                }
            },
        };
    }
    public List<Type> SupportedTypes { get; } = new List<Type>()
    {
        typeof(bool)
    };
}
