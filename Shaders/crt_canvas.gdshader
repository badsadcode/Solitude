// Full-screen CRT treatment for a camera feed shown as a 2D overlay.
shader_type canvas_item;

uniform float noise_amt = 0.04;
uniform float signal : hint_range(0.0, 1.0) = 0.9;
uniform vec4 phosphor : source_color = vec4(0.78, 1.0, 0.88, 1.0);
uniform float desaturate = 0.5;

float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

void fragment() {
	vec2 cc = UV - 0.5;
	vec2 uv = 0.5 + cc * (1.0 + 0.1 * dot(cc, cc));
	float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
	float frame = floor(mod(TIME * 18.0, 4096.0));
	uv.x += (hash(vec2(floor(uv.y * 144.0), frame)) - 0.5) * (1.0 - signal) * 0.04;
	vec3 c = texture(TEXTURE, clamp(uv, vec2(0.0), vec2(1.0))).rgb;
	float lum = dot(c, vec3(0.3, 0.59, 0.11));
	c = mix(c, vec3(lum) * phosphor.rgb, desaturate);
	c *= 0.8 + 0.2 * sin(uv.y * 144.0 * 3.14159);
	c += vec3(hash(floor(UV * vec2(320.0, 240.0)) + vec2(frame * 1.3, frame * 0.7)) * (noise_amt + (1.0 - signal) * 0.14));
	c *= (1.0 - smoothstep(0.3, 0.8, length(cc * 1.2))) * inside;
	COLOR = vec4(clamp(c * 1.2, 0.0, 1.0), 1.0);
}
