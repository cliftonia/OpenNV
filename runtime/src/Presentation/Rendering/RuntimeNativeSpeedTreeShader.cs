namespace OpenNV.Runtime.Presentation.Rendering;

// SpeedTree programs. Lighting, fog and wind direction take the same source
// frame as shader grass so foliage stays consistent across owners. Every
// instance carries its LOD value (1 near, 0 far) in INSTANCE_CUSTOM.x; the
// generated tree and the billboard discard complementary halves of one
// screen-space dither, which crossfades them by alpha test.
internal static class RuntimeNativeSpeedTreeShader
{
    private const string Shared = """
        uniform float source_alpha_cutoff;
        uniform float source_units;
        uniform vec3 source_ambient;
        uniform vec3 source_diffuse_direction;
        uniform vec3 source_diffuse_color;
        uniform float source_directional_scale;
        uniform vec3 source_fog_color;
        uniform vec3 source_fog; // far, far-near, power
        varying float tree_fog;
        varying float tree_lod;

        vec3 tree_sun() { return vec3(source_diffuse_direction.x, source_diffuse_direction.z, -source_diffuse_direction.y); }
        float tree_fog_amount(vec3 view) {
            return pow(1.0 - clamp((source_fog.x - length(view) / source_units) / source_fog.y, 0.0, 1.0), source_fog.z);
        }
        float tree_dither(vec2 pixel) { return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715)))); }
        """;

    // Generated geometry: the four wind matrices act in tree space and are
    // weighted per vertex (CUSTOM0: shade or lightness, weight, matrix, phase).
    private const string Generated = Shared + """
        uniform mat3 tree_wind[4];
        uniform sampler2D source_texture : source_color, filter_linear_mipmap, repeat_enable;
        varying vec3 tree_light;

        vec3 tree_wind_offset(vec3 point, vec4 data) {
            int matrix = clamp(int(round(data.z)), 0, 3);
            return data.y * (tree_wind[matrix] * point - point);
        }
        """;

    internal const string Billboard = "shader_type spatial;\n" +
        "render_mode unshaded, depth_draw_opaque, cull_disabled, fog_disabled, skip_vertex_transform;\n" + Shared + """
        uniform sampler2D source_billboard : filter_linear_mipmap, repeat_disable;
        uniform vec2 source_size; // billboard width, height (game units)
        uniform vec3 source_motion; // rock Hz, rustle Hz, leaf dimming
        uniform vec4 source_wind; // direction.xy, magnitude, seconds
        varying vec3 tree_light;

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
            float wrap = clamp(dot(tree_sun(), normal) * 0.5 + 0.5, 0.0, 1.0);
            float dimming = mix(1.0 - source_motion.z, 1.0, height);
            tree_light = (source_ambient + source_diffuse_color * wrap * source_directional_scale) * dimming;
            vec4 view = VIEW_MATRIX * vec4(point, 1.0);
            tree_fog = tree_fog_amount(view.xyz);
            tree_lod = INSTANCE_CUSTOM.x;
            VERTEX = view.xyz;
            NORMAL = (VIEW_MATRIX * vec4(normal, 0.0)).xyz;
        }
        void fragment() {
            if (tree_dither(FRAGCOORD.xy) < tree_lod) discard;
            vec4 texel = texture(source_billboard, UV);
            if (texel.a <= source_alpha_cutoff) discard;
            ALBEDO = mix(texel.rgb * tree_light, source_fog_color, tree_fog);
        }
        """;

    internal const string Branch = "shader_type spatial;\n" +
        "render_mode unshaded, depth_draw_opaque, cull_disabled, fog_disabled, skip_vertex_transform;\n" + Generated + """
        void vertex() {
            vec3 local = VERTEX + tree_wind_offset(VERTEX, CUSTOM0);
            vec3 normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
            float diffuse = clamp(dot(normal, tree_sun()), 0.0, 1.0);
            tree_light = (source_ambient + source_diffuse_color * diffuse * source_directional_scale) * CUSTOM0.x;
            vec4 view = VIEW_MATRIX * (MODEL_MATRIX * vec4(local, 1.0));
            tree_fog = tree_fog_amount(view.xyz);
            tree_lod = INSTANCE_CUSTOM.x;
            VERTEX = view.xyz;
            NORMAL = (VIEW_MATRIX * vec4(normal, 0.0)).xyz;
        }
        void fragment() {
            if (tree_dither(FRAGCOORD.xy) >= tree_lod) discard;
            ALBEDO = mix(texture(source_texture, UV).rgb * tree_light, source_fog_color, tree_fog);
        }
        """;

    // Leaf cards: four vertices share the leaf centre; UV2 holds the corner
    // offset in the card's (right, up) plane. Rock spins the corner within the
    // card, rustle turns the camera basis about the vertical, and the leaf
    // curvature bends the card normal toward a sphere.
    internal const string Leaf = "shader_type spatial;\n" +
        "render_mode unshaded, depth_draw_opaque, cull_disabled, fog_disabled, skip_vertex_transform;\n" + Generated + """
        uniform vec3 tree_rock; // amplitude (radians), phase, scale
        uniform vec3 tree_rustle;
        uniform float tree_curvature;
        uniform float tree_leaf_dimming;

        float tree_wrap(float angle) { return mod(angle + PI, 2.0 * PI) - PI; }

        void vertex() {
            vec3 centre = (MODEL_MATRIX * vec4(VERTEX + tree_wind_offset(VERTEX, CUSTOM0), 1.0)).xyz;
            float scale = length(MODEL_MATRIX[0].xyz);
            float rock = tree_wrap(tree_rock.x * tree_rock.z * sin(PI * (CUSTOM0.w + tree_rock.y)));
            float rustle = tree_wrap(tree_rustle.x * tree_rustle.z * sin(PI * (CUSTOM0.w + tree_rustle.y)));
            vec2 corner = UV2 * scale;
            corner = vec2(cos(rock) * corner.x - sin(rock) * corner.y, sin(rock) * corner.x + cos(rock) * corner.y);
            vec3 right = INV_VIEW_MATRIX[0].xyz;
            vec3 up = INV_VIEW_MATRIX[1].xyz;
            mat2 turn = mat2(vec2(cos(rustle), sin(rustle)), vec2(-sin(rustle), cos(rustle)));
            right.xz = turn * right.xz;
            up.xz = turn * up.xz;
            vec3 offset = corner.x * right + corner.y * up;
            vec3 normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
            if (dot(offset, offset) > 1e-10) normal = normalize(normalize(offset) * tree_curvature + normal);
            float diffuse = clamp(dot(normal, tree_sun()), 0.0, 1.0);
            float lightness = 1.0 - tree_leaf_dimming * (1.0 - CUSTOM0.x);
            tree_light = lightness * (diffuse * source_diffuse_color * source_directional_scale + source_ambient);
            vec4 view = VIEW_MATRIX * vec4(centre + offset, 1.0);
            tree_fog = tree_fog_amount(view.xyz);
            tree_lod = INSTANCE_CUSTOM.x;
            VERTEX = view.xyz;
            NORMAL = (VIEW_MATRIX * vec4(normal, 0.0)).xyz;
        }
        void fragment() {
            if (tree_dither(FRAGCOORD.xy) >= tree_lod) discard;
            vec4 texel = texture(source_texture, UV);
            if (texel.a <= source_alpha_cutoff) discard;
            ALBEDO = mix(texel.rgb * tree_light, source_fog_color, tree_fog);
        }
        """;
}
