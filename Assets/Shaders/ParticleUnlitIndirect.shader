Shader "ParticleLife/ParticleUnlitIndirect"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _ParticlePositionScale;
            StructuredBuffer<float4> _ParticleColor;

            struct Attributes
            {
                float3 positionOS : POSITION;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color : COLOR0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float4 ps = _ParticlePositionScale[IN.instanceID];
                float3 worldPos = float3(IN.positionOS.xy * ps.z + ps.xy, 0);
                VertexPositionInputs vpi = GetVertexPositionInputs(worldPos);
                OUT.positionHCS = vpi.positionCS;
                OUT.color = _ParticleColor[IN.instanceID];
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                return IN.color;
            }
            ENDHLSL
        }
    }
}
