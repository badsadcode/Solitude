// Bad Ideas Works studio splash: the logo, deep under an alien sea.
// It starts as a murky, blocky shape in dark water; a sonar ping sweeps out from the centre and
// everything the ring passes resolves into sharp, glowing pixels. Caustics ripple over the white,
// light shafts lean in from above, marine snow drifts down. PS1 colour banding on top.
shader_type canvas_item;

uniform sampler2D logo : filter_nearest;
uniform float t = 0.0;          // seconds since the splash began
uniform float aspect = 1.7778;  // screen width / height
uniform float img_aspect = 1.3333;
uniform float ping_at = 1.4;    // when the ring leaves the centre
uniform float fade = 1.0;       // 0 = black, 1 = full

float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float caustic(vec2 q, float time) {
	float c = 0.0;
	for (int i = 0; i < 3; i++) {
		q += vec2(sin(q.y * 1.3 + time * 0.7), cos(q.x * 1.1 - time * 0.6));
		c += sin(q.x) * cos(q.y);
	}
	return clamp(abs(c) * 0.45, 0.0, 1.0);
}

void fragment() {
	vec2 p = (UV - 0.5) * vec2(aspect, 1.0);

	// --- water: deep blue-teal, lighter toward the surface, swaying light shafts
	vec3 water = mix(vec3(0.0, 0.012, 0.02), vec3(0.0, 0.07, 0.085), pow(1.0 - UV.y, 1.6));
	float shafts = 0.0;
	for (int i = 0; i < 3; i++) {
		float fi = float(i);
		shafts += pow(max(0.0, sin(p.x * (5.0 + fi * 2.3) + p.y * 1.6 + sin(t * 0.25 + fi) * 1.7)), 14.0);
	}
	water += vec3(0.1, 0.35, 0.38) * shafts * 0.07 * pow(1.0 - UV.y, 2.0);

	// --- the logo, fitted to 64% of the screen height, wobbling as if seen through water
	float H = 0.64;
	vec2 luv = vec2(p.x / (H * img_aspect), p.y / H) + 0.5;
	luv += vec2(sin(luv.y * 17.0 + t * 1.3), cos(luv.x * 13.0 + t * 1.1)) * 0.0035;

	// --- the ping: a ring growing from the centre; behind it, the logo is resolved
	float r = max(0.0, (t - ping_at) * 0.85);
	float d = length(p);
	float res = smoothstep(r, r - 0.18, d) * step(ping_at, t);
	float blocks = mix(22.0, 170.0, res);
	vec2 grid = vec2(blocks * img_aspect, blocks);
	vec2 q = (floor(luv * grid) + 0.5) / grid;
	float inside = step(0.0, q.x) * step(q.x, 1.0) * step(0.0, q.y) * step(q.y, 1.0);
	float m = texture(logo, q).r * inside;

	float glow = caustic(luv * vec2(img_aspect, 1.0) * 7.0, t);
	vec3 ink = vec3(0.6, 1.0, 0.86) * (0.72 + 0.45 * glow);
	float bright = mix(0.16 + 0.05 * sin(t * 2.0), 1.0, res);
	vec3 col = mix(water, ink * bright, m);

	// --- the ring itself, and a faint second echo
	float ring = exp(-abs(d - r) * 70.0) * step(ping_at, t) * exp(-max(0.0, t - ping_at) * 0.6);
	float echo = exp(-abs(d - r * 0.6) * 90.0) * step(ping_at + 0.4, t) * exp(-max(0.0, t - ping_at) * 1.2) * 0.4;
	col += vec3(0.35, 1.0, 0.8) * (ring * 0.7 + echo);

	// --- marine snow, three depths, drifting down and a little sideways
	for (int i = 0; i < 3; i++) {
		float fi = float(i);
		float scale = 90.0 + fi * 70.0;
		vec2 sp = UV * vec2(aspect, 1.0) * scale + vec2(t * (0.6 + fi * 0.4), -t * (2.0 + fi * 1.5));
		vec2 cell = floor(sp);
		float h = hash(cell + fi * 17.0);
		vec2 f = fract(sp) - vec2(hash(cell + 3.1), hash(cell + 7.7));
		float dot_ = step(0.985 - fi * 0.004, h) * smoothstep(0.12, 0.0, length(f));
		col += vec3(0.5, 0.75, 0.7) * dot_ * (0.25 + 0.15 * fi);
	}

	// --- vignette, PS1 colour banding, fade
	col *= 1.0 - smoothstep(0.55, 1.15, length(p * vec2(0.85, 1.1)));
	col = floor(col * 31.0 + hash(floor(FRAGCOORD.xy / 2.0)) * 0.9) / 31.0;
	COLOR = vec4(col * fade, 1.0);
}
