// A porthole in a station wall: black water, faint drifting snow, and now and then something
// large and slow passing the glass, darker than the dark. Unshaded: it's the outside, not lit by us.
shader_type spatial;
render_mode unshaded, cull_back;

float hash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }

void fragment() {
	vec2 uv = UV - 0.5;
	float r = length(uv);
	if (r > 0.5) discard;
	float t = TIME;
	// Deep water: a faint blue-green at the top fading to nothing.
	vec3 col = mix(vec3(0.0, 0.012, 0.018), vec3(0.01, 0.05, 0.06), smoothstep(0.5, -0.5, uv.y));
	// Marine snow drifting slowly down and sideways.
	for (int i = 0; i < 3; i++) {
		float fi = float(i);
		vec2 p = (UV + vec2(t * (0.006 + fi * 0.003), t * (0.02 + fi * 0.01))) * (22.0 + fi * 14.0);
		vec2 cell = floor(p);
		vec2 f = fract(p) - 0.5;
		float h = hash(cell + fi * 17.0);
		float dotv = step(0.93, h) * smoothstep(0.12, 0.0, length(f - (vec2(hash(cell + 3.1), hash(cell + 7.7)) - 0.5) * 0.6));
		col += vec3(0.5, 0.6, 0.6) * dotv * (0.25 + fi * 0.1);
	}
	// Every ~40 s a long dark shape slides past.
	float pass = fract(t / 41.0);
	float x = (pass - 0.5) * 3.0;
	float body = smoothstep(0.35, 0.1, abs(uv.y + 0.05 * sin(t * 0.3))) * smoothstep(0.9, 0.2, abs(uv.x - x));
	col *= 1.0 - body * 0.9;
	// Thick old glass: darker and greener at the rim.
	col *= mix(1.0, 0.4, smoothstep(0.3, 0.5, r));
	ALBEDO = col;
}
