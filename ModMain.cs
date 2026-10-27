using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using UnityEngine;

namespace APTrackerRemember;

/// <summary>
/// Small companion mod for Ixrec.ArchipelagoRandomizer: when you back out of a category in the
/// AP Tracker, put the cursor back on that category instead of the top of the list.
/// Everything is done by reflection at runtime, so there is no compile-time dependency on the AP mod,
/// and nothing happens if the AP mod isn't installed.
/// </summary>
public class ModMain : ModBehaviour
{
    internal static ModMain Instance;

    private const string ApModUniqueName = "Ixrec.ArchipelagoRandomizer";
    private const string ChecklistModeTypeName = "ArchipelagoRandomizer.InGameTracker.APChecklistMode";

    private void Start()
    {
        Instance = this;
        if (!ModHelper.Interaction.ModExists(ApModUniqueName))
            return; // AP Randomizer isn't installed/enabled: do nothing
        StartCoroutine(PatchWhenLoaded());
    }

    private IEnumerator PatchWhenLoaded()
    {
        // The AP mod's DLL may not be loaded yet when our Start() runs, so retry for a few seconds.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            Type type = AccessTools.TypeByName(ChecklistModeTypeName);
            if (type != null)
            {
                Patches.Install(type);
                yield break;
            }
            yield return new WaitForSeconds(0.5f);
        }
        Log("Couldn't find the AP Randomizer tracker code. Nothing was patched.", MessageType.Warning);
    }

    internal static void Log(string message, MessageType type = MessageType.Info)
    {
        Instance?.ModHelper.Console.WriteLine("[APTrackerRemember] " + message, type);
    }
}

internal static class Patches
{
    private static bool installed;

    private static FieldInfo inChecklistField;
    private static FieldInfo selectionWrapperField;
    private static FieldInfo displayedCategoriesField;
    private static MethodInfo selectItemMethod;
    private static MethodInfo getIndexMethod;
    private static MethodInfo setIndexMethod;
    private static MethodInfo updateListUIMethod;

    internal static void Install(Type modeType)
    {
        if (installed) return;

        try
        {
            MethodBase target = AccessTools.Method(modeType, "OpenSelectionMode");
            inChecklistField = AccessTools.Field(modeType, "IsInChecklist");
            selectionWrapperField = AccessTools.Field(modeType, "SelectionWrapper");
            displayedCategoriesField = AccessTools.Field(modeType, "displayedCategories");
            selectItemMethod = AccessTools.Method(modeType, "SelectSelectionItem", new[] { typeof(int) });

            if (selectionWrapperField != null)
            {
                Type wrapperType = selectionWrapperField.FieldType;
                getIndexMethod = AccessTools.Method(wrapperType, "GetSelectedIndex");
                setIndexMethod = AccessTools.Method(wrapperType, "SetSelectedIndex", new[] { typeof(int) });
                updateListUIMethod = AccessTools.Method(wrapperType, "UpdateListUI");
            }

            string missing = "";
            if (target == null) missing += " OpenSelectionMode";
            if (inChecklistField == null) missing += " IsInChecklist";
            if (selectionWrapperField == null) missing += " SelectionWrapper";
            if (displayedCategoriesField == null) missing += " displayedCategories";
            if (selectItemMethod == null) missing += " SelectSelectionItem";
            if (getIndexMethod == null) missing += " GetSelectedIndex";
            if (setIndexMethod == null) missing += " SetSelectedIndex";
            if (updateListUIMethod == null) missing += " UpdateListUI";
            if (missing != "")
            {
                ModMain.Log("The AP mod's code has changed, couldn't find:" + missing + ". Nothing was patched.", MessageType.Warning);
                return;
            }

            Harmony harmony = new Harmony("Local.APTrackerRememberPosition");
            harmony.Patch(
                target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(Prefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(Postfix))));

            installed = true;
            ModMain.Log("Patched the AP Tracker.", MessageType.Success);
        }
        catch (Exception e)
        {
            ModMain.Log("Failed to patch the AP Tracker: " + e, MessageType.Error);
        }
    }

    // IsInChecklist is only true when we're backing out of a category (the AP mod sets it to false
    // before opening the tracker fresh), so that's the one case where we remember the cursor.
    private static void Prefix(object __instance, out int __state)
    {
        __state = -1;
        try
        {
            if ((bool)inChecklistField.GetValue(__instance))
            {
                object wrapper = selectionWrapperField.GetValue(__instance);
                if (wrapper != null)
                    __state = (int)getIndexMethod.Invoke(wrapper, null);
            }
        }
        catch (Exception e)
        {
            ModMain.Log("Prefix failed: " + e, MessageType.Error);
            __state = -1;
        }
    }

    private static void Postfix(object __instance, int __state)
    {
        if (__state < 0) return;
        try
        {
            ICollection categories = displayedCategoriesField.GetValue(__instance) as ICollection;
            if (categories == null || __state >= categories.Count) return;

            object wrapper = selectionWrapperField.GetValue(__instance);
            if (wrapper == null) return;

            setIndexMethod.Invoke(wrapper, new object[] { __state });
            updateListUIMethod.Invoke(wrapper, null);
            selectItemMethod.Invoke(__instance, new object[] { __state });
        }
        catch (Exception e)
        {
            ModMain.Log("Postfix failed: " + e, MessageType.Error);
        }
    }
}
