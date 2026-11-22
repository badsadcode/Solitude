// Floodwater inside the boat (Tom: "water slowly filling up the interior, with a nice vertex shader").
// A subdivided plane at the water line: layered travelling waves in the vertex shader (stronger
// while a breach is pouring in), a churned spot where the spray lands, foam along the walls,
// fresnel + a hard PS1 specular glint. Cull disabled so it reads from below as well.
shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_always, diffuse_lambert, specular_schlick_ggx;

uniform vec4 deep_color : source_color = vec4(0.02, 0.09, 0.1, 0.78);
uniform vec4 shallow_color : source_color = vec4(0.1, 0.28, 0.3, 0.55);
uniform vec4 foam_color : source_color = vec4(0.75, 0.88, 0.9, 1.0);
uniform float turbulence = 0.0;        // 0 calm .. 1 a breach pouring in
uniform vec2 inflow = vec2(0.0, 0.0);  // local xz where the spray hits the water
uniform vec2 half_size = vec2(2.4, 3.5);
uniform float jitter = 260.0;

varying vec2 local_xz;
varying float crest;

float waves(vec2 p, float t) {
	float h = 0.0;
	h += sin(p.x * 2.1 + t * 1.3) * 0.012;
	h += sin(p.y * 1.7 - t * 1.1 + p.x * 0.6) * 0.014;
	h += sin((p.x + p.y) * 3.7 + t * 2.3) * 0.006;
	return h;
}

void vertex() {
	local_xz = VERTEX.xz;
	float t = TIME;
	float h = waves(VERTEX.xz, t) * (0.6 + turbulence * 1.8);
	// Rings spreading from where the water pours in.
	float d = distance(VERTEX.xz, inflow);
	h += sin(d * 9.0 - t * 7.0) * 0.025 * turbulence * exp(-d * 0.55);
	VERTEX.y += h;
	crest = h;

	vec4 clip = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	if (jitter > 0.0 && clip.w > 0.0001) {
		vec2 grid = vec2(jitter, jitter * 0.5625);
		vec2 ndc = floor(clip.xy / clip.w * grid + 0.5) / grid;
		clip.xy = ndc * clip.w;
	}
	POSITION = clip;
}

float hash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }

void fragment() {
	float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 3.0);
	vec4 col = mix(deep_color, shallow_color, clamp(crest * 18.0 + 0.4, 0.0, 1.0));

	// Foam: along the walls, on crests, and in a churned ring around the inflow.
	// Foam hugs the side walls only; at the room ends the water carries on through the doorway.
	float wall = 1.0 - smoothstep(0.0, 0.18, half_size.x - abs(local_xz.x));
	float churn = turbulence * (1.0 - smoothstep(0.0, 1.2, distance(local_xz, inflow)));
	float speck = step(0.72, hash(floor(local_xz * 22.0) + floor(TIME * 6.0)));
	float foam = clamp(wall * 0.6 + churn * speck + smoothstep(0.02, 0.05, crest) * 0.5, 0.0, 1.0);

	ALBEDO = mix(col.rgb, foam_color.rgb, foam * 0.7);
	ALPHA = clamp(col.a + foam * 0.2 + fres * 0.15, 0.0, 0.95);
	ROUGHNESS = 0.06;
	METALLIC = 0.2;
	SPECULAR = 0.9;
	EMISSION = vec3(0.0, 0.012, 0.014) + foam_color.rgb * foam * 0.04;
}
