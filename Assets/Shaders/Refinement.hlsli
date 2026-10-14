#ifndef SERT_REFINEMENT
#define SERT_REFINEMENT
struct RefinedSample{uint2 pixel,identity;float4 position,normal;float4 radiance[4];float4 sun,metadata,guide;uint4 key;uint guideQuality,padding0,padding1,padding2;};
#endif
