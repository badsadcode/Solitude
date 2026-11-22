// In-world CRT screen showing a live camera feed (ViewportTexture).
//
// IMPORTANT: the feed is sampled as linear light (source_color). Deep-water blacks are
// ~0.003 linear, so all CRT effects (noise, scanlines, phosphor) are done in display
// (gamma) space and converted back to linear at the end. Doing them in linear space
// washes the picture out into grey static.
shader_type spatial;
render_mode unshaded, cull_back, shadows_disabled, fog_disabled;

uniform sampler2D feed : source_color, filter_nearest;
uniform float brightness = 1.3;
uniform float noise_amt = 0.05;
uniform float signal : hint_range(0.0, 1.0) = 0.9;
uniform vec4 phosphor : source_color = vec4(0.78, 1.0, 0.88, 1.0);
uniform float powered = 1.0;
uniform float selected = 0.0;
uniform float scan_lines = 140.0;
uniform float desaturate = 0.55;

// Dave Hoskins' hash12: stable on all GPUs, no sin().
float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

void fragment() {
	vec2 cc = UV - 0.5;
	vec2 uv = 0.5 + cc * (1.0 + 0.18 * dot(cc, cc));
	float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);

	float frame = floor(mod(TIME * 18.0, 4096.0));
	float line = floor(uv.y * scan_lines);
	uv.x += (hash(vec2(line, frame)) - 0.5) * (1.0 - signal) * 0.05;
	uv.x += sin(uv.y * 6.0 + mod(TIME, 100.0) * 2.3) * 0.0015;

	// Linear → display space.
	vec3 c = pow(texture(feed, clamp(uv, vec2(0.0), vec2(1.0))).rgb, vec3(1.0 / 2.2));
	vec3 tint = pow(phosphor.rgb, vec3(1.0 / 2.2));
	float lum = dot(c, vec3(0.3, 0.59, 0.11));
	c = mix(c, vec3(lum) * tint, desaturate);
	c *= 0.82 + 0.18 * sin(uv.y * scan_lines * 3.14159);

	// Snow, centred around zero, heavier as the signal degrades.
	float n = hash(floor(UV * vec2(197.0, 143.0)) + vec2(frame * 1.7, frame * 0.3)) - 0.5;
	c += vec3(n * (noise_amt + (1.0 - signal) * 0.3));
	c += vec3(0.02, 0.035, 0.028); // phosphor never quite goes black

	float vig = 1.0 - smoothstep(0.3, 0.75, length(cc * 1.3));
	c *= vig * inside;

	float border = step(0.47, max(abs(cc.x), abs(cc.y)));
	c = mix(c, vec3(1.0, 0.55, 0.1), border * selected);

	// Display → linear.
	ALBEDO = pow(clamp(c, 0.0, 1.0), vec3(2.2)) * brightness * powered;
}
