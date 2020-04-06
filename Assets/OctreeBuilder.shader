Shader "Hidden/VCT/OctreeBuilder" {
    Properties
    {
    }

    SubShader {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            ZTest off
            ZWrite off
            Cull off
            ColorMask 0

            CGPROGRAM
            #pragma target 5.0
            #include "UnityCG.cginc"
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile   _ ORIENTATION_2 ORIENTATION_3

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
            };

            #define OCTREE_ROOT  8
            RWStructuredBuffer<int> Octree : register(u1);
            float4 _VCT_Scale;
            uint _VCT_TreeLevels;


            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 pos = i.vertex.xyz;
#if defined(UNITY_REVERSED_Z)
                pos.z = 1.0 - pos.z;
#endif
                pos *= _VCT_Scale.xxy;
                pos += _VCT_Scale.zzw;
#ifdef ORIENTATION_2
                pos = pos.yzx;
#endif
#ifdef ORIENTATION_3
                pos = pos.zxy;
#endif
                /* now xyz should be in octree coordinates, [0-1]^3 */

                if (any((pos * (1.0 - pos)) <= 0.0))
                    return fixed4(0, 0, 0, 0);

                int index = OCTREE_ROOT;
                int next_index;
                for (uint i = 0; i <= _VCT_TreeLevels; i++)
                {
                    index += pos.x >= 0.5 ? 1 : 0;
                    index += pos.y >= 0.5 ? 2 : 0;
                    index += pos.z >= 0.5 ? 4 : 0;

                    next_index = Octree[index];
                    if (next_index <= 0)
                    {
                        InterlockedCompareExchange(Octree[index], 0, -1, next_index);
                        if (next_index == 0)
                        {
                            next_index = Octree.IncrementCounter() * 8 + 16;
                            Octree[index] = next_index;
                        }
                        else
                        {
#if defined(ORIENTATION_2)
                            Octree[1] = 1;
#elif defined(ORIENTATION_3)
                            Octree[2] = 1;
#else
                            Octree[0] = 1;
#endif
                            return fixed4(0, 0, 0, 0);   /* dummy result, ignored */
                        }
                    }

                    index = next_index;
                    pos = frac(pos * 2.0);
                }

                /* dummy result, ignored */
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}
