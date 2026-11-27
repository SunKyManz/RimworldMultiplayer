using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Client.Util;
using RimWorld;

namespace MultiplayerLoader;

public static partial class SyncRituals
{
    private static readonly MethodInfo syncMethod = AccessTools.Method("Multiplayer.Client.Sync:Method");

    private static void RegisterRitualSyncMethods(Action<string, Type, Action<ISyncMethod>> register)
    {
        var ritualRolesSerializer = Serializer.New<IEnumerable<RitualRole>, (PawnRitualRoleSelectionWidget roleSelectionWidget, string Key)>(
            RitualRoleWriter<PawnRitualRoleSelectionWidget, RitualRole>,
            RitualRoleReader<PawnRitualRoleSelectionWidget, RitualRole>
        );
        var psychicRitualRolesSerializer = Serializer.New<IEnumerable<PsychicRitualRoleDef>, (PawnPsychicRitualRoleSelectionWidget roleSelectionWidget, string Key)>(
            RitualRoleWriter<PawnPsychicRitualRoleSelectionWidget, PsychicRitualRoleDef>,
            RitualRoleReader<PawnPsychicRitualRoleSelectionWidget, PsychicRitualRoleDef>
        );

        // Keep one ordered registration list for both version-specific patch paths.
        register(
            nameof(PawnRoleSelectionWidgetBase<RitualRole>.TryAssignReplace),
            typeof(PawnRitualRoleSelectionWidget),
            method => method.TransformArgument(1, ritualRolesSerializer));
        register(
            nameof(PawnRoleSelectionWidgetBase<PsychicRitualRoleDef>.TryAssignReplace),
            typeof(PawnPsychicRitualRoleSelectionWidget),
            method => method.TransformArgument(1, psychicRitualRolesSerializer));

        register(
            nameof(PawnRoleSelectionWidgetBase<RitualRole>.TryAssignAnyRole),
            typeof(PawnRitualRoleSelectionWidget),
            method => method.SetPreInvoke(RefreshRoleGroups<RitualRole>));
        register(
            nameof(PawnRoleSelectionWidgetBase<PsychicRitualRoleDef>.TryAssignAnyRole),
            typeof(PawnPsychicRitualRoleSelectionWidget),
            method => method.SetPreInvoke(RefreshRoleGroups<PsychicRitualRoleDef>));

        register(
            nameof(PawnRoleSelectionWidgetBase<RitualRole>.TryAssign),
            typeof(PawnRitualRoleSelectionWidget),
            method => method.TransformArgument(1, ritualRolesSerializer));
        register(
            nameof(PawnRoleSelectionWidgetBase<PsychicRitualRoleDef>.TryAssign),
            typeof(PawnPsychicRitualRoleSelectionWidget),
            method => method.TransformArgument(1, psychicRitualRolesSerializer));

        register(
            MpMethodUtil.GetLambda(
                typeof(PawnRoleSelectionWidgetBase<ILordJobRole>),
                nameof(PawnRoleSelectionWidgetBase<RitualRole>.DrawPawnListInternal),
                lambdaOrdinal: 7).Name,
            typeof(PawnRitualRoleSelectionWidget),
            null); // Roles right click delegate (try assign spectate)
        register(
            MpMethodUtil.GetLambda(
                typeof(PawnRoleSelectionWidgetBase<ILordJobRole>),
                nameof(PawnRoleSelectionWidgetBase<PsychicRitualRoleDef>.DrawPawnListInternal),
                lambdaOrdinal: 7).Name,
            typeof(PawnPsychicRitualRoleSelectionWidget),
            null); // Roles right click delegate (try assign spectate)

        register(
            MpMethodUtil.GetLambda(
                typeof(PawnRoleSelectionWidgetBase<ILordJobRole>),
                nameof(PawnRoleSelectionWidgetBase<RitualRole>.DrawPawnListInternal),
                lambdaOrdinal: 2).Name,
            typeof(PawnRitualRoleSelectionWidget),
            method => method.SetPreInvoke(RefreshRoleGroups<RitualRole>)); // Not participating left click delegate (try assign any role or spectate)
        register(
            MpMethodUtil.GetLambda(
                typeof(PawnRoleSelectionWidgetBase<ILordJobRole>),
                nameof(PawnRoleSelectionWidgetBase<PsychicRitualRoleDef>.DrawPawnListInternal),
                lambdaOrdinal: 2).Name,
            typeof(PawnPsychicRitualRoleSelectionWidget),
            method => method.SetPreInvoke(RefreshRoleGroups<PsychicRitualRoleDef>)); // Not participating left click delegate (try assign any role or spectate)
    }

    // rolesGroupedTmp is only filled when the dialog is drawn, so it's empty on clients that don't have it open.
    private static void RefreshRoleGroups<RoleType>(object target, object[] _) where RoleType : class, ILordJobRole
    {
        PawnRoleSelectionWidgetBase<RoleType>.rolesGroupedTmp.Clear();
        PawnRoleSelectionWidgetBase<RoleType>.rolesGroupedTmp.AddRange(((PawnRoleSelectionWidgetBase<RoleType>)target).assignments.RoleGroups());
    }

    private static (WidgetType roleSelectionWidget, string Key) RitualRoleWriter<WidgetType, RoleType>(IEnumerable<RoleType> roles, object target, object[] _)
        where WidgetType : PawnRoleSelectionWidgetBase<RoleType>
        where RoleType : class, ILordJobRole
    {
        var roleSelectionWidget = (WidgetType)target;
        return (roleSelectionWidget, roleSelectionWidget.assignments.RoleGroups().FirstOrDefault(group => group.SequenceEqual(roles))?.Key);
    }

    private static IEnumerable<RoleType> RitualRoleReader<WidgetType, RoleType>((WidgetType roleSelectionWidget, string Key) data)
        where WidgetType : PawnRoleSelectionWidgetBase<RoleType>
        where RoleType : class, ILordJobRole
    {
        return data.roleSelectionWidget.assignments.RoleGroups().FirstOrDefault(group => group.Key == data.Key);
    }
}
