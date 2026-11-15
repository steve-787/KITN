using System;
using System.Reflection;
using Brutal.Numerics;

public class PlannerBodyProxy : DispatchProxy
{
    public double Mu = 3.986004418e14, Radius = 6.371e6, Soi = 9.24e8;
    public string Id = "Earth";
    public double AtmosphereRadius;
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "get_Mu") return Mu;
        if (method.Name == "get_Mass") return Mu / 6.6743e-11;
        if (method.Name == "get_Id") return Id;
        if (method.Name == "GetAtmosphereRadius") return AtmosphereRadius;
        if (method.Name == "get_MeanRadius" || method.Name == "get_MinTerrainRadius" || method.Name == "get_MaxTerrainRadius"
            || method.Name == "GetNearSurfaceRadius") return Radius;
        if (method.Name == "get_SphereOfInfluence") return Soi;
        if (method.ReturnType == typeof(doubleQuat)) return doubleQuat.Identity;
        if (method.ReturnType == typeof(double3)) return double3.Zero;
        if (method.ReturnType == typeof(void)) return null;
        return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
