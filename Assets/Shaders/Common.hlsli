#ifndef SERT_COMMON
#define SERT_COMMON
static const float PI = 3.14159265359;
struct Vertex { float3 p; float u; float3 n; float v; };
struct Triangle { uint a,b,c,material; };
struct Node { float3 lo; uint left; float3 hi; uint count; };
struct Instance { row_major float4x4 world, inverse; float3 lo; uint root; float3 hi; uint identity; uint materialBase,revision,flags,pad; };
struct Material { float3 color; float rough; float3 emission; float metal; uint atlasIndex,flags,revision,pad; float4 uv; float4 optical,opticalUv; };
struct Light { float3 p; float range; float3 radiance; float falloff; float3 direction; float cone; uint kind,identity,revision,triIndex; uint instance,atlasIndex,flags,pad; };
struct Reservoir { uint light,revision; float2 uv; float sum,target; uint count,age; };
cbuffer Parameters : register(b1) {
    uint2 FullSize, Size;
    uint VertexCount, TriangleCount, InstanceCount, NodeCount;
    uint LightCount, MaterialCount, TlasRoot, Frame;
    float4 SunDirection, SunColor, Sky;
    row_major float4x4 PreviousViewProjection;
    float4 CameraDelta;
    uint HistoryValid, Iteration, TreeLeaves, PoseCount;
    float Exposure, PrimaryRange, DiffuseRange, SpecularRange;
    uint CollectStatistics, ShadowFlags; float ShadowBias; uint ShadowDebugView;
    float LampRadius, SolarRadius, GlassTransmission, RefinementFraction;
    uint BiasMode, SamplingMode, SamplingSeed, FarSunValid;
    row_major float4x4 FarSunMatrix;
    uint RefinementCapacity, RefinementPixels, QualityPadding0, QualityPadding1;
};
StructuredBuffer<Vertex> Vertices : register(t16);
StructuredBuffer<Triangle> Triangles : register(t17);
StructuredBuffer<Node> Nodes : register(t18);
StructuredBuffer<Instance> Instances : register(t19);
StructuredBuffer<Material> Materials : register(t20);
StructuredBuffer<Light> Lights : register(t21);
Texture2DArray<float4> ColorMetal : register(t22);
Texture2DArray<float4> Extensions : register(t23); // emissivity, roughness, opacity, AO
Texture2D<float4> PositionDepth : register(t24);
Texture2D<float4> NormalRoughness : register(t25);
Texture2D<float4> AlbedoMetal : register(t26);
Texture2D<uint2> Identity : register(t27);
StructuredBuffer<Reservoir> Reservoirs : register(t28);
Texture2DArray<float4> Radiance : register(t29);
Texture2D<float4> PreviousPosition : register(t30);
Texture2D<float4> Metadata : register(t31);
Texture2D<float4> Moments : register(t32);
StructuredBuffer<float> LightTree : register(t33);
Texture2D<float4> SunVisibility : register(t34);
Texture2D<float2> Motion : register(t35);
Texture2D<float4> PreviousNormal : register(t36);
Texture2D<uint2> PreviousIdentity : register(t37);
Texture2DArray<float4> PreviousRadiance : register(t38);
TextureCube<float4> Environment : register(t43);
Texture2D<float> NativeSunShadows : register(t44);
Texture2D<float4> PreviousMetadata : register(t47);
Texture2D<float4> GeometryGuide:register(t49);
Texture2DArray<uint4> BlueNoise:register(t50);
Texture2D<uint4> SourceKey:register(t51);
Texture2D<uint4> PreviousSourceKey:register(t52);
Texture2D<float4> ShadowGuide:register(t53);
Texture2D<float4> PreviousShadowGuide:register(t54);
Texture2D<float4> FastHistory:register(t55);
Texture2D<float4> PreviousFastHistory:register(t56);
Texture2D<float4> SunFast:register(t57);
Texture2D<float4> PreviousSunFast:register(t58);
Texture2D<float> FarSunDepth:register(t59);
Texture2D<uint> GuideQuality:register(t66);
StructuredBuffer<float> VertexErrors:register(t67);
SamplerState LinearWrap : register(s0);
SamplerState LinearClamp : register(s1);
uint hash(uint v) { v ^= v >> 16; v *= 0x7feb352d; v ^= v >> 15; v *= 0x846ca68b; return v ^ (v >> 16); }
float random(inout uint s) { s = hash(s + 0x9e3779b9); return (s >> 8) * (1.0 / 16777216.0); }
float luma(float3 v) { return dot(v, float3(.2126,.7152,.0722)); }
float3 safe(float3 v) { return all(isfinite(v)) ? max(v,0) : 0; }
float3 unpackNormal(uint packed) {float2 f=float2(packed&65535,packed>>16)/65535*2-1;float3 n=float3(f,1-abs(f.x)-abs(f.y));if(n.z<0)n.xy=(1-abs(n.yx))*float2(n.x>=0?1:-1,n.y>=0?1:-1);return normalize(n);}
float gamma(uint n){float v=n*1.1920928955078125e-7;return v/(1-v);}
float rayError(float3 p){return max(1e-6,dot(abs(p),1)*gamma(5));}
float biasFor(float3 p,float error){return max(max(error,rayError(p)),BiasMode==1?ShadowBias:0);}
float3 surfaceOffset(float3 p,float3 n,float3 d,float error){return p+n*(dot(n,d)>=0?1:-1)*biasFor(p,error);}
float3 offset(float3 p, float3 n) { return surfaceOffset(p,n,n,rayError(p)); }
float2 sampleContinuous(uint2 p,uint dimension){
    uint shift=hash(dimension*1571+SamplingSeed)^hash((Frame>>3)*7477);uint2 q=(p+uint2(shift,shift>>8))&15;uint frame=(Frame+(shift>>16))&7;
    if(SamplingMode==1){uint seed=hash(p.y*FullSize.x+p.x^hash(Frame+SamplingSeed)^hash(dimension));return float2(random(seed),random(seed));}
    uint4 v=BlueNoise.Load(int4(q,frame,0));uint c=dimension&3;return (float2(v[c],v[(c+1)&3])+.5)/256;
}
float3 basis(float3 n, float3 local) {
    float3 t = normalize(cross(abs(n.z)<.999 ? float3(0,0,1) : float3(0,1,0),n));
    return t*local.x + cross(n,t)*local.y + n*local.z;
}
float3 cosine(float3 n, float2 u) { float r=sqrt(u.x), a=2*PI*u.y; return basis(n,float3(r*cos(a),r*sin(a),sqrt(1-u.x))); }
bool box(float3 o,float3 d,float3 lo,float3 hi,float maxT,out float nearT) {
    float a=0,b=maxT;
    [unroll] for(uint j=0;j<3;j++) {
        if(abs(d[j])<1e-12) { if(o[j]<lo[j] || o[j]>hi[j]) {nearT=0;return false;} }
        else { float2 t=float2(lo[j]-o[j],hi[j]-o[j])/d[j]; a=max(a,min(t.x,t.y)); b=min(b,max(t.x,t.y)); }
    }
    nearT=a;return b>=a;
}
bool triIndex(float3 o,float3 d,Triangle t,float maxT,out float distance,out float2 uv) {
    float3 a=Vertices[t.a].p,b=Vertices[t.b].p,c=Vertices[t.c].p;
    float3 e=b-a,f=c-a,p=cross(d,f); float det=dot(e,p);
    distance=0; uv=0; if(abs(det)<1e-9)return false;
    float3 v=o-a;float inv=1/det; uv.x=dot(v,p)*inv;
    float3 q=cross(v,e);uv.y=dot(d,q)*inv; distance=dot(f,q)*inv;
    return uv.x>=0 && uv.y>=0 && uv.x+uv.y<=1 && distance>=max(1e-7,rayError(o)*.25/max(length(d),1e-12)) && distance<maxT;
}
float2 textureUv(Triangle t,float2 bary) { Vertex a=Vertices[t.a],b=Vertices[t.b],c=Vertices[t.c];return float2(a.u,a.v)*(1-bary.x-bary.y)+float2(b.u,b.v)*bary.x+float2(c.u,c.v)*bary.y; }
void sampleMaterial(Triangle t,float2 bary,Material m,out float4 cm,out float4 ext) {
    float2 uv=textureUv(t,bary);cm=0;ext=0;
    if((m.flags&4)==0){cm=ColorMetal.SampleLevel(LinearWrap,float3(uv,m.atlasIndex),0);cm.rgb*=m.color;ext=Extensions.SampleLevel(LinearWrap,float3(uv,m.atlasIndex),0);ext.x=saturate(ext.x-1.0/255);return;}
    Vertex a=Vertices[t.a],b=Vertices[t.b],c=Vertices[t.c];float3 w=float3(1-bary.x-bary.y,bary);
    float3 pos=a.p*w.x+b.p*w.y+c.p*w.z,normal=normalize(a.n*w.x+b.n*w.y+c.n*w.z);
    float3 axis=pow(abs(normal),4);axis/=max(dot(axis,1),1e-8);float3 blend=float3(uv,max(0,1-uv.x-uv.y));
    uint xz[3]={m.atlasIndex,uint(m.uv.y),uint(m.uv.w)}, yy[3]={uint(m.uv.x),uint(m.uv.z),m.pad};
    [unroll]for(uint k=0;k<3;k++){
        float3 coord=pos*m.color[k];float4 tc=0,te=0;
        tc+=ColorMetal.SampleLevel(LinearWrap,float3(coord.zy,xz[k]),0)*axis.x;tc+=ColorMetal.SampleLevel(LinearWrap,float3(coord.xz,yy[k]),0)*axis.y;tc+=ColorMetal.SampleLevel(LinearWrap,float3(coord.xy,xz[k]),0)*axis.z;
        te+=Extensions.SampleLevel(LinearWrap,float3(coord.zy,xz[k]),0)*axis.x;te+=Extensions.SampleLevel(LinearWrap,float3(coord.xz,yy[k]),0)*axis.y;te+=Extensions.SampleLevel(LinearWrap,float3(coord.xy,xz[k]),0)*axis.z;
        cm+=tc*blend[k];ext+=te*blend[k];
    }
}
struct Hit { uint status,triIndex,instance; float distance; float2 uv; };
// status: 0 certified miss, 1 hit, 2 incomplete coverage/material, 3 bounded traversal overflow.
#ifdef SERT_RAY_QUERY
// Hardware traversal of the same scene (Vulkan ray query). The TLAS custom index is the instance index,
// InstanceTriangleBase maps a primitive back to its triangle, every triangle is a non-opaque candidate,
// and unknown-coverage instances are AABB candidates, so each candidate meets the software rules below.
RaytracingAccelerationStructure SceneTlas:register(t68);
StructuredBuffer<uint> InstanceTriangleBase:register(t69);
float queryMinimum(float3 o,float3 d){return max(1e-7,rayError(o)*.25/max(length(d),1e-12));}
Hit traceScene(float3 o,float3 d,float maxT,bool shadow,bool primary) {
    Hit h=(Hit)0;h.distance=maxT;
    if(InstanceCount==0 || (!shadow && length(o)+maxT>256)) {h.status=2;return h;}
    bool beyondCoverage=false;
    if(shadow) {
        float b=dot(o,d),c=dot(o,o)-256*256,disc=b*b-c;
        if(c>0 || disc<0){h.status=2;return h;}
        float horizon=max(0,-b+sqrt(disc));beyondCoverage=maxT>horizon;
        h.distance=min(maxT,horizon);
    }
    float missing=h.distance+1;
    RayDesc ray;ray.Origin=o;ray.Direction=d;ray.TMin=queryMinimum(o,d);ray.TMax=h.distance;
    RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> q;q.TraceRayInline(SceneTlas,0,0xFF,ray);
    [loop] while(q.Proceed()) {
        uint k=q.CandidateInstanceID();if(k>=InstanceCount){h.status=3;return h;}Instance inst=Instances[k];
        if(q.CandidateType()!=CANDIDATE_NON_OPAQUE_TRIANGLE){float nearT;if(box(o,d,inst.lo,inst.hi,h.distance,nearT))missing=min(missing,nearT);continue;}
        uint t=InstanceTriangleBase[k]+q.CandidatePrimitiveIndex();if(t>=TriangleCount){h.status=3;return h;}
        Triangle tri=Triangles[t];float distance=q.CandidateTriangleRayT();float2 bary=q.CandidateTriangleBarycentrics();
        Material mat=Materials[inst.materialBase+tri.material];
        if(primary && (mat.flags&32)!=0)continue;
        if(shadow && (mat.flags&1)!=0 && (mat.flags&8)!=0){missing=min(missing,distance);continue;}
        float opacity=Extensions.SampleLevel(LinearWrap,float3(textureUv(tri,bary),mat.atlasIndex),0).z;
        if((mat.flags&1)!=0 && opacity<.5)continue;
        if((mat.flags&2)!=0 || (mat.flags&16)!=0){missing=min(missing,distance);continue;}
        q.CommitNonOpaqueTriangleHit();h.status=1;h.distance=distance;h.triIndex=t;h.instance=k;h.uv=bary;
        if(shadow){q.Abort();return h;}
    }
    if(missing<=h.distance || (shadow && beyondCoverage))h.status=2;return h;
}
#else
Hit traceScene(float3 o,float3 d,float maxT,bool shadow,bool primary) {
    Hit h=(Hit)0;h.distance=maxT;
    if(InstanceCount==0 || (!shadow && length(o)+maxT>256)) {h.status=2;return h;}
    bool beyondCoverage=false;
    if(shadow) {
        // Clip to the resident sphere, but still accept a known blocker inside it.
        float b=dot(o,d),c=dot(o,o)-256*256,disc=b*b-c;
        if(c>0 || disc<0){h.status=2;return h;}
        float horizon=max(0,-b+sqrt(disc));beyondCoverage=maxT>horizon;
        h.distance=min(maxT,horizon);
    }
    uint stack[64],sp=1;stack[0]=TlasRoot;float missing=h.distance+1;uint visits=0,intersections=0;
    [loop] while(sp>0) {
        if(++visits>8192) {h.status=3;return h;}
        uint ni=stack[--sp];if(ni>=NodeCount){h.status=3;return h;} Node n=Nodes[ni];float nearT;
        if(!box(o,d,n.lo,n.hi,h.distance,nearT))continue;
        if(n.count==0) {if(sp+2>64){h.status=3;return h;}stack[sp++]=n.left;stack[sp++]=n.left+1;continue;}
        [loop] for(uint k=n.left;k<n.left+n.count;k++) {
            if(k>=InstanceCount){h.status=3;return h;}Instance inst=Instances[k];
            if(!box(o,d,inst.lo,inst.hi,h.distance,nearT))continue;
            if(inst.root==0xffffffff) {missing=min(missing,nearT);continue;}
            float3 localO=mul(float4(o,1),inst.inverse).xyz,localD=mul(float4(d,0),inst.inverse).xyz;
            uint bs[64],bp=1;bs[0]=inst.root;
            [loop] while(bp>0) {
                if(++visits>8192){h.status=3;return h;}uint bi=bs[--bp];if(bi>=NodeCount){h.status=3;return h;}Node bn=Nodes[bi];
                if(!box(localO,localD,bn.lo,bn.hi,h.distance,nearT))continue;
                if(bn.count==0){if(bp+2>64){h.status=3;return h;}bs[bp++]=bn.left;bs[bp++]=bn.left+1;continue;}
                [loop] for(uint t=bn.left;t<bn.left+bn.count;t++) {
                    if(++intersections>4096||t>=TriangleCount){h.status=3;return h;}Triangle tri=Triangles[t];float distance;float2 bary;
                    if(!triIndex(localO,localD,tri,h.distance,distance,bary))continue;
                    Material mat=Materials[inst.materialBase+tri.material];
                    // Native glass is composited after opaque lighting. Confirmation
                    // follows the opaque GBuffer; continuation still fails closed.
                    if(primary && (mat.flags&32)!=0)continue;
                    if(shadow && (mat.flags&1)!=0 && (mat.flags&8)!=0){missing=min(missing,distance);continue;}
                    float opacity=Extensions.SampleLevel(LinearWrap,float3(textureUv(tri,bary),mat.atlasIndex),0).z;
                    if((mat.flags&1)!=0 && opacity<.5)continue;
                    if((mat.flags&2)!=0 || (mat.flags&16)!=0){missing=min(missing,distance);continue;}
                    h.status=1;h.distance=distance;h.triIndex=t;h.instance=k;h.uv=bary;
                    // Visibility needs any opaque blocker, even if an unknown box was nearer.
                    if(shadow)return h;
                }
            }
        }
    }
    if(missing<=h.distance || (shadow && beyondCoverage))h.status=2;return h;
}
#endif
Hit trace(float3 o,float3 d,float maxT){return traceScene(o,d,maxT,false,false);}
Hit tracePrimary(float3 o,float3 d,float maxT){return traceScene(o,d,maxT,false,true);}
Hit traceShadow(float3 o,float3 d,float maxT){return traceScene(o,d,maxT,true,false);}
void hitSurface(Hit h,out float3 p,out float3 n,out float3 albedo,out float metal,out float rough,out float3 emission,out uint2 id) {
    Instance inst=Instances[h.instance];Triangle t=Triangles[h.triIndex];Material m=Materials[inst.materialBase+t.material];
    Vertex a=Vertices[t.a],b=Vertices[t.b],c=Vertices[t.c];float3 bary=float3(1-h.uv.x-h.uv.y,h.uv);
    p=mul(float4(a.p*bary.x+b.p*bary.y+c.p*bary.z,1),inst.world).xyz;
    n=normalize(mul(a.n*bary.x+b.n*bary.y+c.n*bary.z,transpose((float3x3)inst.inverse)));
    float4 cm,ext;sampleMaterial(t,h.uv,m,cm,ext);
    albedo=cm.rgb;metal=saturate(cm.a);rough=clamp(ext.y,.02,1);emission=albedo*(1-metal)*ext.x*m.emission*SunColor.w;
    id=uint2(inst.identity,hash(inst.materialBase+t.material)^m.revision^inst.revision);
}
float triangleError(Triangle t,Instance inst){
    float3 a=Vertices[t.a].p,b=Vertices[t.b].p,c=Vertices[t.c].p;
    float3 magnitude=abs(a)+abs(b-a)+abs(c-a),local=magnitude*gamma(9);float3x3 transformMagnitude=abs((float3x3)inst.world);
    float3 bound=mul(local,transformMagnitude)+gamma(5)*(mul(magnitude,transformMagnitude)+abs(inst.world[3].xyz));
    float skin=max(VertexErrors[t.a],max(VertexErrors[t.b],VertexErrors[t.c]));
    return max(1e-6,dot(bound,1)+skin*dot(mul(float3(1,1,1),transformMagnitude),1));
}
void hitGeometry(Hit h,out float3 p,out float3 geometric,out float error){
    Triangle t=Triangles[h.triIndex];Instance inst=Instances[h.instance];float3 a=Vertices[t.a].p,b=Vertices[t.b].p,c=Vertices[t.c].p;
    p=mul(float4(a*(1-h.uv.x-h.uv.y)+b*h.uv.x+c*h.uv.y,1),inst.world).xyz;
    float3 wa=mul(float4(a,1),inst.world).xyz,wb=mul(float4(b,1),inst.world).xyz,wc=mul(float4(c,1),inst.world).xyz;
    geometric=normalize(cross(wb-wa,wc-wa));error=triangleError(t,inst);
}
// GGX BRDF with Smith masking. Outputs diffuse irradiance and specular radiance separately.
void brdf(float3 n,float3 v,float3 l,float3 color,float metal,float rough,out float3 diffuse,out float3 specular) {
    float nl=saturate(dot(n,l)),nv=max(saturate(dot(n,v)),1e-4);float3 h=normalize(v+l);
    float nh=saturate(dot(n,h)),vh=saturate(dot(v,h)),a=max(.02,rough*rough),a2=a*a;
    float den=nh*nh*(a2-1)+1;float D=a2/(PI*den*den);
    float k=(rough+1)*(rough+1)/8;float G=nv/(nv*(1-k)+k)*nl/(nl*(1-k)+k);
    float3 f0=lerp(.04.xxx,color,metal),F=f0+(1-f0)*pow(1-vh,5);
    diffuse=(1-F)*(1-metal)*nl/PI;specular=D*G*F/max(4*nv,1e-5);
}
float3 sampleLobe(float3 n,float3 v,float rough,bool spec,float2 u,out float pdf) {
    if(!spec){float3 d=cosine(n,u);pdf=max(dot(n,d),0)/PI;return d;}
    float a=max(.02,rough*rough),ct=sqrt((1-u.x)/(1+(a*a-1)*u.x)),st=sqrt(max(0,1-ct*ct)),phi=2*PI*u.y;
    float3 h=basis(n,float3(st*cos(phi),st*sin(phi),ct));float3 d=reflect(-v,h);
    float den=ct*ct*(a*a-1)+1;pdf=a*a/(PI*den*den)*ct/max(4*abs(dot(v,h)),1e-5);return d;
}
bool compatible(uint2 p,uint2 old) {
    if(any(p>=Size)||any(old>=Size))return false;
    float4 a=PositionDepth[p],b=PreviousPosition[old];
    return a.w>0 && b.w>0 && all(Identity[p]==PreviousIdentity[old]) &&
        abs(a.w-b.w)<max(.02,a.w*.01) && dot(NormalRoughness[p].xyz,PreviousNormal[old].xyz)>.92;
}
int2 previousPixel(uint2 p) {
    float2 motion=Motion.Load(int3(min(uint2((float2(p)+.5)*FullSize/Size),FullSize-1),0));
    return int2(floor(float2(p)+.5+motion*Size/FullSize));
}
int2 previousSpecularPixel(uint2 p,int2 primary) {
    float distance=Metadata[p].y,rough=NormalRoughness[p].w;
    if(distance<=0 || rough>=.5)return primary;
    float3 pos=PositionDepth[p].xyz,virtualPoint=pos+normalize(pos)*distance;
    float4 a=mul(float4(pos+CameraDelta.xyz,1),PreviousViewProjection),b=mul(float4(virtualPoint+CameraDelta.xyz,1),PreviousViewProjection);
    if(a.w<=0||b.w<=0)return primary;
    float2 delta=(b.xy/b.w-a.xy/a.w)*float2(.5,-.5)*Size;
    return primary+int2(round(clamp(delta,-8,8)*(1-rough/.5)));
}
uint selectLight(inout uint rng,out float pdf) {
    float choice=random(rng);uint i;
    float total=LightTree[1];
    if(choice<.05 || total<=0){i=min(uint(random(rng)*LightCount),LightCount-1);}
    else {uint n=1;float r=random(rng)*total;[loop]while(n<TreeLeaves){float left=LightTree[n*2];n*=2;if(r>=left){r-=left;n++;}}i=min(n-TreeLeaves,LightCount-1);}
    pdf=.05/LightCount+(total>0?.95*LightTree[TreeLeaves+i]/total:.95/LightCount);return i;
}
void evaluateLight(uint index,float2 uv,float3 p,float3 n,float3 v,float3 albedo,float metal,float rough,
    out float3 ld,out float3 ls,out float3 direction,out float distance,out float conditionalPdf) {
    Light l=Lights[index];float3 energy=0;conditionalPdf=1;distance=128;
    if(l.kind<2) {
        float3 delta=l.p-p;distance=length(delta);direction=delta/max(distance,1e-5);
        float falloff=1/(.001+pow(l.falloff/1.5,3));float attenuation=pow(saturate(1-pow(distance/max(l.range,.01),4)),3);
        if(l.kind==1)attenuation*=pow(saturate((dot(-direction,normalize(l.direction))-l.cone)/max(1-l.cone,1e-4)),2);
        energy=l.radiance*attenuation;
        if(l.kind==1 && (l.flags&1)!=0){float3 forward=normalize(l.direction),up=unpackNormal(l.triIndex),right=normalize(cross(forward,up)),toSurface=-direction;float tangent=sqrt(max(0,1-l.cone*l.cone))/max(l.cone,1e-5);
            float2 st=float2(dot(toSurface,right),-dot(toSurface,up))/(2*max(dot(toSurface,forward),1e-5)*tangent)+.5;energy*=ColorMetal.SampleLevel(LinearClamp,float3(st,l.pad),0).rgb;}
    } else if(l.kind==2) {
        Instance inst=Instances[l.instance];Triangle t=Triangles[l.triIndex];Material m=Materials[l.atlasIndex];
        float3 a=mul(float4(Vertices[t.a].p,1),inst.world).xyz,b=mul(float4(Vertices[t.b].p,1),inst.world).xyz,c=mul(float4(Vertices[t.c].p,1),inst.world).xyz;
        float s=sqrt(uv.x);float2 bary=float2(s*(1-uv.y),s*uv.y);float3 source=a*(1-s)+b*bary.x+c*bary.y;
        float3 delta=source-p;distance=length(delta);direction=delta/max(distance,1e-5);float3 crossEdge=cross(b-a,c-a);float area=length(crossEdge)*.5;
        float cosLight=abs(dot(normalize(crossEdge),-direction));conditionalPdf=distance*distance/max(area*cosLight,1e-8);
        float4 cm,ex;sampleMaterial(t,bary,m,cm,ex);energy=(m.flags&8)!=0?0:cm.rgb*(1-saturate(cm.a))*ex.x*m.emission*SunColor.w;
    } else if(l.kind==4) {
        float radius=sqrt(uv.x)*l.range,angle=2*PI*uv.y;float3 source=l.p+basis(normalize(l.direction),float3(radius*cos(angle),radius*sin(angle),0));
        float3 delta=source-p;distance=length(delta);direction=delta/max(distance,1e-5);float cosineSource=abs(dot(normalize(l.direction),-direction));
        conditionalPdf=distance*distance/max(l.cone*cosineSource,1e-8);energy=l.radiance;
        if((l.flags&1)!=0){float2 st=float2(.5+.5*sqrt(uv.x)*cos(angle),.5+.5*sqrt(uv.x)*sin(angle));float4 texel=ColorMetal.SampleLevel(LinearWrap,float3(st,l.atlasIndex),0);energy*=texel.rgb*texel.a;}
    } else {direction=cosine(n,uv);conditionalPdf=max(dot(n,direction),0)/PI;energy=Environment.SampleLevel(LinearClamp,float3(-direction.x,direction.y,direction.z),0).rgb;}
    if(l.kind<2)rough=clamp(1-(1-rough)*asfloat(l.atlasIndex),.02,1);
    // Finite sources use their actual endpoint; resident coverage decides shadow validity.
    float3 bd,bs;brdf(n,v,direction,albedo,metal,rough,bd,bs);ld=safe(energy*bd/conditionalPdf);ls=safe(energy*bs/conditionalPdf);
    if(l.kind<2){float normalized=distance/max(l.range,.01),falloff=1/(.001+pow(l.falloff/1.5,3));ld*=asfloat(l.instance)/(1+falloff*normalized*normalized);}
}
float target(uint light,float2 uv,float3 p,float3 n,float3 v,float3 color,float metal,float rough) {
    float3 d,s,direction;float distance,pdf;evaluateLight(light,uv,p,n,v,color,metal,rough,d,s,direction,distance,pdf);
    return max(0,luma(d*color+s));
}
void add(inout Reservoir r,uint light,float2 uv,float weight,float t,uint count,inout uint rng) {
    r.count+=count;if(weight<=0 || !isfinite(weight))return;r.sum+=weight;
    if(random(rng)*r.sum<weight){r.light=light;r.revision=hash(Lights[light].revision^Lights[light].identity);r.uv=uv;r.target=t;}
}
#endif
