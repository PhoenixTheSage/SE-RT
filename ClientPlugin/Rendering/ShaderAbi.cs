using System;
using System.Runtime.InteropServices;
using SharpDX.D3DCompiler;

namespace SERT.Rendering;

internal static class ShaderAbi
{
    internal static void Verify(byte[] bytecode)
    {
        using(var reflection=new ShaderReflection(bytecode))
        {
            var parameters=reflection.GetConstantBuffer("Parameters");
            if(parameters.Description.Size!=Marshal.SizeOf<Parameters>())throw new InvalidOperationException("SE-RT Parameters CPU/HLSL size mismatch");
            foreach(var pair in new[]{new[]{"LampRadius","LampRadius"},new[]{"BiasMode","BiasMode"},new[]{"FarSunMatrix","FarSunMatrix"},new[]{"RefinementCapacity","RefinementCapacity"}})
                if(parameters.GetVariable(pair[0]).Description.StartOffset!=Marshal.OffsetOf<Parameters>(pair[1]).ToInt32())throw new InvalidOperationException("SE-RT Parameters offset mismatch: "+pair[0]);
            if(reflection.GetResourceBindingDescription("Parameters").BindPoint!=1)throw new InvalidOperationException("SE-RT Parameters binding mismatch");
        }
    }
}
