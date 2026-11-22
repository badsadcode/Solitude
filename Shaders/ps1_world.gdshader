// PS1-flavoured world material: clip-space vertex snapping, chunky nearest textures,
// vertex colours, optional triplanar projection, emission and sway (for flora).
shader_type spatial;
render_mode diffuse_lambert, specular_schlick_ggx;

uniform sampler2D albedo_tex : source_color, filter_nearest, repeat_enable;
uniform vec4 tint : source_color = vec4(1.0);
uniform float uv_scale = 1.0;
uniform bool triplanar = false;
uniform float tri_scale = 0.25;
// Vertex snap grid in clip space. ~ the "resolution" of vertex positions. 0 disables.
uniform float jitter = 180.0;
uniform float roughness_v : hint_range(0.0, 1.0) = 0.9;
uniform float metallic_v : hint_range(0.0, 1.0) = 0.0;
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform float emission_energy = 0.0;
// Emission is gated by vertex colour alpha < 1 when this is set (lets one mesh mix lit + glowing parts).
uniform bool emission_from_alpha = false;
uniform float sway = 0.0;
uniform float pulse = 0.0;

varying vec3 world_pos;
varying vec3 world_nrm;

void vertex() {
	vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	if (sway > 0.0) {
		float h = max(VERTEX.y, 0.0);
		float ph = wp.x * 0.37 + wp.z * 0.29;
		VERTEX.x += sin(TIME * 0.7 + ph) * sway * h * h * 0.05;
		VERTEX.z += cos(TIME * 0.53 + ph * 1.3) * sway * h * h * 0.05;
		wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	}
	world_pos = wp;
	world_nrm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);

	vec4 clip = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	if (jitter > 0.0 && clip.w > 0.0001) {
		vec2 grid = vec2(jitter, jitter * 0.5625);
		vec2 ndc = clip.xy / clip.w;
		ndc = floor(ndc * grid + 0.5) / grid;
		clip.xy = ndc * clip.w;
	}
	POSITION = clip;
}

void fragment() {
	vec3 col;
	if (triplanar) {
		vec3 b = pow(abs(world_nrm), vec3(4.0));
		b /= (b.x + b.y + b.z);
		vec3 tx = texture(albedo_tex, world_pos.zy * tri_scale).rgb;
		vec3 ty = texture(albedo_tex, world_pos.xz * tri_scale).rgb;
		vec3 tz = texture(albedo_tex, world_pos.xy * tri_scale).rgb;
		col = tx * b.x + ty * b.y + tz * b.z;
	} else {
		col = texture(albedo_tex, UV * uv_scale).rgb;
	}
	col *= COLOR.rgb;
	ALBEDO = col * tint.rgb;
	ROUGHNESS = roughness_v;
	METALLIC = metallic_v;
	float glow = emission_energy;
	if (emission_from_alpha) {
		glow *= step(COLOR.a, 0.99);
	}
	if (pulse > 0.0) {
		glow *= 0.65 + 0.35 * sin(TIME * pulse + world_pos.x * 0.7 + world_pos.z * 0.5);
	}
	vec3 ecol = emission_from_alpha ? COLOR.rgb * emission_color.rgb * 1.6 : emission_color.rgb;
	EMISSION = ecol * glow;
}
