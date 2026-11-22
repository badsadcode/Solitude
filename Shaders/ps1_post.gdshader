// Full-screen post stack applied to the low-res game viewport:
// chromatic aberration, film grain, ordered dither + colour quantisation,
// faint scanlines, vignette, damage flash and a gentle water wobble.
shader_type canvas_item;

uniform float color_depth = 28.0;
uniform float dither_strength = 1.0;
uniform float vignette = 0.55;
uniform float grain = 0.035;
uniform float aberration = 0.0018;
uniform float scanline = 0.06;
uniform float damage = 0.0;
uniform float wobble = 0.0;
uniform float fade = 1.0;
uniform vec4 tint : source_color = vec4(1.0);

float bayer2(vec2 a) { a = floor(a); return fract(a.x / 2.0 + a.y * a.y * 0.75); }
float bayer4(vec2 a) { return bayer2(0.5 * a) * 0.25 + bayer2(a); }
float bayer8(vec2 a) { return bayer4(0.5 * a) * 0.25 + bayer2(a); }

float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

void fragment() {
	vec2 uv = UV;
	uv.x += sin(uv.y * 23.0 + TIME * 1.1) * wobble * 0.0025;
	uv.y += cos(uv.x * 17.0 + TIME * 0.8) * wobble * 0.0015;

	vec2 d = uv - 0.5;
	float ab = aberration * (1.0 + damage * 6.0);
	vec3 c;
	c.r = texture(TEXTURE, uv + d * ab * 2.0).r;
	c.g = texture(TEXTURE, uv).g;
	c.b = texture(TEXTURE, uv - d * ab * 2.0).b;

	vec2 px = floor(uv / TEXTURE_PIXEL_SIZE);

	c += (hash(px + fract(TIME * 7.13) * 91.0) - 0.5) * grain;

	// Ordered dither, then quantise: the classic 15-bit console look.
	c += (bayer8(px) - 0.5) * dither_strength / color_depth;
	c = floor(c * color_depth + 0.5) / color_depth;

	c *= 1.0 - scanline * mod(px.y, 2.0);

	float v = 1.0 - smoothstep(0.35, 0.85, length(d * vec2(1.0, 0.8)) * (0.9 + vignette * 0.6));
	c *= mix(1.0, v, vignette + damage * 0.4);

	float lum = dot(c, vec3(0.299, 0.587, 0.114));
	c = mix(c, vec3(lum * 1.4, lum * 0.15, lum * 0.1), damage * 0.6);

	COLOR = vec4(clamp(c * tint.rgb * fade, 0.0, 1.0), 1.0);
}
