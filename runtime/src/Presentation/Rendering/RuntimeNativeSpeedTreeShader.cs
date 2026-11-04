namespace OpenNV.Runtime.Presentation.Rendering;

// SpeedTree billboard: one upright quad per instance turned about the
// vertical axis toward the camera. Lighting, fog and wind take the same
// source frame as shader grass so foliage stays consistent across owners.
internal static class RuntimeNativeSpeedTreeShader
{
    internal const string Code = """
        shader_type spatial;
        render_mode unshaded, depth_draw_opaque, cull_disabled, fog_disabled, skip_vertex_transform;
        uniform sampler2D source_billboard : filter_linear_mipmap, repeat_disable;
        uniform float source_alpha_cutoff;
        uniform float source_units;
        uniform vec2 source_size; // billboard width, height (game units)
        uniform vec3 source_motion; // rock Hz, rustle Hz, leaf dimming
        uniform vec4 source_wind; // direction.xy, magnitude, seconds
        uniform vec3 source_ambient;
        uniform vec3 source_diffuse_direction;
        uniform vec3 source_diffuse_color;
        uniform float source_directional_scale;
        uniform vec3 source_fog_color;
        uniform vec3 source_fog; // far, far-near, power
        varying vec3 tree_light;
        varying float tree_fog;

        void vertex() {
            vec3 origin = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
            float scale = length(MODEL_MATRIX[0].xyz);
            vec2 facing = CAMERA_POSITION_WORLD.xz - origin.xz;
            facing = dot(facing, facing) > 1e-6 ? normalize(facing) : vec2(0.0, 1.0);
            vec3 right = vec3(facing.y, 0.0, -facing.x);
            float height = VERTEX.y;
            float metres = source_units * scale;
            vec3 point = origin + right * (VERTEX.x * source_size.x * metres) + vec3(0.0, height * source_size.y * metres, 0.0);
            // Rock bends the whole crown; rustle is a smaller, faster, per-corner flutter.
            float phase = dot(origin.xz, vec2(0.071, 0.053));
            float rock = sin(6.2831853 * source_motion.x * source_wind.w + phase);
            float rustle = sin(6.2831853 * source_motion.y * source_wind.w + phase * 3.0 + VERTEX.x * 5.0);
            vec3 direction = vec3(source_wind.x, 0.0, -source_wind.y);
            point += direction * (height * height) * source_wind.z * metres * (rock + 0.3 * rustle);
            vec3 normal = normalize(vec3(facing.x, 0.75, facing.y));
            vec3 sun = vec3(source_diffuse_direction.x, source_diffuse_direction.z, -source_diffuse_direction.y);
            float wrap = clamp(dot(sun, normal) * 0.5 + 0.5, 0.0, 1.0);
            float dimming = mix(1.0 - source_motion.z, 1.0, height);
            tree_light = (source_ambient + source_diffuse_color * wrap * source_directional_scale) * dimming;
            vec4 view = VIEW_MATRIX * vec4(point, 1.0);
            tree_fog = pow(1.0 - clamp((source_fog.x - length(view.xyz) / source_units) / source_fog.y, 0.0, 1.0), source_fog.z);
            VERTEX = view.xyz;
            NORMAL = (VIEW_MATRIX * vec4(normal, 0.0)).xyz;
        }
        void fragment() {
            vec4 texel = texture(source_billboard, UV);
            if (texel.a <= source_alpha_cutoff) discard;
            ALBEDO = mix(texel.rgb * tree_light, source_fog_color, tree_fog);
        }
        """;
}
