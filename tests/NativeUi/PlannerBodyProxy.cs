using System;
using System.Reflection;
using Brutal.Numerics;

public class PlannerBodyProxy : DispatchProxy
{
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "get_Mu") return 3.986004418e14;
        if (method.Name == "get_Mass") return 3.986004418e14 / 6.6743e-11;
        if (method.Name == "get_Id") return "Earth";
        if (method.Name == "get_MeanRadius" || method.Name == "get_MinTerrainRadius" || method.Name == "get_MaxTerrainRadius"
            || method.Name == "GetNearSurfaceRadius") return 6.371e6;
        if (method.Name == "get_SphereOfInfluence") return 9.24e8;
        if (method.ReturnType == typeof(doubleQuat)) return doubleQuat.Identity;
        if (method.ReturnType == typeof(double3)) return double3.Zero;
        if (method.ReturnType == typeof(void)) return null;
        return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
