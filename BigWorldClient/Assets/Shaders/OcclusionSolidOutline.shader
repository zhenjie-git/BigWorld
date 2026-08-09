Shader "Custom/Standard_WithXRayOcclusion"
{
    Properties
    {
        // 原版 Standard 所有参数完美保留
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Color ("Main Color", Color) = (1,1,1,1)
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1.0
        
        // 新增：被遮挡时的透视发光颜色
        _XRayColor ("透视发光颜色 (XRay Color)", Color) = (0,0.8,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }

        // ============================================
        // Pass 1：被遮挡时渲染的透视发光（无膨胀）
        // ============================================
        Pass
        {
            Name "XRAY"
            ZTest GEqual  // 核心：深度大于等于当前深度时才绘制（即被遮挡时）
            ZWrite Off    // 不写入深度
            Cull Front    // 剔除正面，显示模型内部，不用把模型放大
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
            
            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 pos : SV_POSITION;
            };
            
            sampler2D _MainTex;
            float4 _XRayColor;
            
            v2f vert (appdata v)
            {
                v2f o;
                // 【关键】没有任何顶点膨胀操作，绝对不会变粗
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                // 会穿透显示角色的贴图颜色，并和发光颜色混合
                fixed4 col = tex2D(_MainTex, i.uv) * _XRayColor;
                return col;
            }
            ENDCG
        }

        // ============================================
        // Pass 2：本体正常渲染（完美复刻标准材质）
        // ============================================
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        
        struct Input
        {
            float2 uv_MainTex;
            float2 uv_BumpMap;
        };
        
        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _Color;
        half _Metallic;
        half _Glossiness;
        float _BumpScale;
        
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
            
            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            
            // 【重新加回了法线贴图】没有这一行，角色绝对没有立体感
            o.Normal = UnpackNormal(tex2D (_BumpMap, IN.uv_BumpMap));
            o.Normal.xy *= _BumpScale;
            
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Standard"
}