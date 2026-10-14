using System;
using System.Numerics;
using SERT.Core;

static class Quality
{
    internal static void Run(Action<bool,string> check)
    {
        var defaults=ShadowSettings.Default;
        check(defaults.BiasMode==ShadowBiasMode.Automatic && defaults.LampRadius==.025f && defaults.SolarDegrees==.266f,"small source and automatic-origin defaults");
        check(new SourceExtent(.25f,0).RadiusAt(.1f)==.025f,"near-source stability clamp");
        check(new SourceExtent(0,0).RadiusAt(10)==0,"zero radius hard-shadow equivalence");
        var budget=new RefinementBudget();
        for(int i=0;i<7;i++)budget.Observe(5,4);
        check(budget.Capacity(1000,.05f)==50,"refinement holds allowance for seven expensive samples");
        budget.Observe(5,4);check(budget.Capacity(1000,.05f)==25,"eighth expensive sample halves allowance");
        for(int i=0;i<59;i++)budget.Observe(2,4);
        check(budget.Multiplier==.5f,"refinement recovery hysteresis");
        budget.Observe(2,4);check(budget.Multiplier==1,"sixtieth cheap sample restores allowance");
        for(int i=0;i<32;i++)budget.Observe(5,4);
        check(budget.Multiplier==0 && budget.Capacity(1000,.1f)==0,"refinement exhausted before resolution response");
        budget.Observe(double.NaN,4);check(budget.Multiplier==0,"invalid GPU measurement cannot restore allowance");
        var tint=MaterialOptics.Transmission(new Vector3(2,1,.5f),.5f,.9f);
        check(Vector3.Distance(tint,new Vector3(.95f,.725f,.6125f))<1e-6,"linear tinted transmission with uncalibrated hue normalization");
        check(MaterialOptics.Transmission(Vector3.One,0,.9f)==Vector3.One,"zero opacity preserves energy");
        check((tint*tint).X<tint.X && (tint*tint).Z<(tint*tint).Y,"distinct glass surfaces multiply RGB attenuation");
        var material=new Material{Flags=2}; var texture=new object();
        object[] descriptor={1,new[]{1f,.5f,.25f,.5f},texture,new[]{0f,0,1,1},19u,true,"thin-glass"};
        object[] variant={null,null,new object[4],null,descriptor};
        check(ReferenceEquals(MaterialOptics.Apply(variant,ref material)[0],texture) && (material.Flags&96)==96 && material.Padding==19,"optical category, revision and sidedness contract");
        descriptor[6]="hologram";MaterialOptics.Apply(variant,ref material);
        check((material.Flags&32)==0,"unsupported optical category remains unknown");
        // Compare the float reconstruction used by our shader against a double reference,
        // with grazing-scale triangles, camera-relative translations and nonuniform shear.
        var rng=new Random(3479);
        for(int test=0;test<2000;test++)
        {
            float F(float scale)=>(float)((rng.NextDouble()*2-1)*scale);
            var a=new Vector3(F(200),F(200),F(200));var b=a+new Vector3(F(3),F(.003f),F(10));var c=a+new Vector3(F(.03f),F(4),F(.1f));
            var m=new Matrix4x4(F(3),F(.2f),F(.1f),0,F(.2f),F(.03f),F(.3f),0,F(.1f),F(.3f),F(30),0,F(256),F(256),F(256),1);
            float u=(float)rng.NextDouble(),v=(float)rng.NextDouble()*(1-u);
            var actual=Vector3.Transform(a*(1-u-v)+b*u+c*v,m);
            double x=a.X*(1-(double)u-v)+b.X*(double)u+c.X*(double)v;
            double y=a.Y*(1-(double)u-v)+b.Y*(double)u+c.Y*(double)v;
            double z=a.Z*(1-(double)u-v)+b.Z*(double)u+c.Z*(double)v;
            double error=Math.Abs(actual.X-(x*m.M11+y*m.M21+z*m.M31+m.M41))+Math.Abs(actual.Y-(x*m.M12+y*m.M22+z*m.M32+m.M42))+Math.Abs(actual.Z-(x*m.M13+y*m.M23+z*m.M33+m.M43));
            check(error<=RayPrecision.PositionError(a,b,c,m),"software affine/reconstruction error bound against double reference");
        }
        // Four weighted affine bone operations against an independent double sum.
        for(int test=0;test<1000;test++)
        {
            float F(float scale)=>(float)((rng.NextDouble()*2-1)*scale);
            var position=new Vector3(F(200),F(.001f),F(200));var actual=Vector3.Zero;double x=0,y=0,z=0,bound=0;
            var weights=new float[4];float total=0;for(int bone=0;bone<4;bone++){weights[bone]=(float)rng.NextDouble();total+=weights[bone];}
            for(int bone=0;bone<4;bone++)
            {
                float weight=weights[bone]/total;
                var m=new Matrix4x4(F(3),F(.2f),F(.1f),0,F(.2f),F(.03f),F(.3f),0,F(.1f),F(.3f),F(30),0,F(256),F(256),F(256),1);
                actual+=Vector3.Transform(position,m)*weight;
                x+=((double)position.X*m.M11+(double)position.Y*m.M21+(double)position.Z*m.M31+m.M41)*weight;
                y+=((double)position.X*m.M12+(double)position.Y*m.M22+(double)position.Z*m.M32+m.M42)*weight;
                z+=((double)position.X*m.M13+(double)position.Y*m.M23+(double)position.Z*m.M33+m.M43)*weight;
                double magnitude=Math.Abs((double)position.X)*(Math.Abs(m.M11)+Math.Abs(m.M12)+Math.Abs(m.M13))+
                    Math.Abs((double)position.Y)*(Math.Abs(m.M21)+Math.Abs(m.M22)+Math.Abs(m.M23))+
                    Math.Abs((double)position.Z)*(Math.Abs(m.M31)+Math.Abs(m.M32)+Math.Abs(m.M33))+
                    Math.Abs(m.M41)+Math.Abs(m.M42)+Math.Abs(m.M43);
                bound+=magnitude*RayPrecision.Gamma(9)*weight;
            }
            check(Math.Abs(actual.X-x)+Math.Abs(actual.Y-y)+Math.Abs(actual.Z-z)<=Math.Max(bound,1e-6),"weighted GPU skinning operation bound against double reference");
        }
    }
}
