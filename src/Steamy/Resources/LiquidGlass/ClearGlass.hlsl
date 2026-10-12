sampler2D input : register(s0);
float2 size : register(c0);
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float radius = size.y * .5;
    float2 p = (uv - .5) * size;
    float2 q = float2(max(abs(p.x) - (size.x * .5 - radius), 0), p.y);
    float distance = length(q);
    float2 normal = float2(q.x * sign(p.x), q.y) / max(distance, .001);
    float edge = smoothstep(radius - 6, radius, distance);
    float2 sampleUv = clamp(uv - normal * edge * 1.8 / size, .002, .998);
    float2 pixel = 2.8 / size;
    float4 color = tex2D(input, sampleUv) * .2;
    color += (tex2D(input, sampleUv + float2(pixel.x, 0)) + tex2D(input, sampleUv - float2(pixel.x, 0))
        + tex2D(input, sampleUv + float2(0, pixel.y)) + tex2D(input, sampleUv - float2(0, pixel.y))) * .12;
    color += (tex2D(input, sampleUv + pixel) + tex2D(input, sampleUv - pixel)
        + tex2D(input, sampleUv + float2(pixel.x, -pixel.y)) + tex2D(input, sampleUv + float2(-pixel.x, pixel.y))) * .08;
    return color;
}
