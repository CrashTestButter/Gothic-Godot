// Gothic objects made after loading, with a texture of their own (the array is fixed once built), lit with the
// baked light of the ground below (tint) plus a little sun shading so shapes read.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform vec3 sun_direction = vec3(0.4, 0.8, 0.3);
instance uniform vec4 tint : source_color = vec4(0.6, 0.6, 0.6, 1.0);
// Daylight for things standing outdoors (tint.a = 1 from the ground below them).
global uniform float sky_light;

varying float shade;

void vertex() {
	vec3 world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	shade = 0.8 + 0.35 * max(dot(world_normal, normalize(sun_direction)), 0.0);
}

void fragment() {
	vec4 tex = texture(albedo_texture, UV);
	ALBEDO = tex.rgb * tint.rgb * shade * mix(1.0, sky_light, tint.a);
	ALPHA = tex.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
}
