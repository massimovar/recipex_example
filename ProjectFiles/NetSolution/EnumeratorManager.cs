#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.RecipeX;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.Alarm;
using Microsoft.VisualBasic;
using System.ComponentModel;
#endregion

public class EnumeratorManager : BaseNetLogic
{
    public override void Start()
    {
        var label = Owner as Label;
        var index = LogicObject.GetVariable("index").Value;
        var enumerationDataType = InformationModel.Get<IUADataType>(LogicObject.GetVariable("enumeration").Value);
        if (enumerationDataType != null)
        {
            var fields = enumerationDataType.EnumDefinition.Fields;
            if (index >= 0 && index < fields.Count)
            {
                var field = fields[index];
                label.LocalizedText = new LocalizedText(label.NodeId.NamespaceIndex,field.DisplayName.TextId);
            }
            else
            {
                Log.Error($"Index {index} out of range for enumeration with {fields.Count} fields.");
        }
        }
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }


}
