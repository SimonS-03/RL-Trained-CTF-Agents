from typing import Any, Callable

from gymnasium.wrappers.vector import RecordEpisodeStatistics

from unity_vecenv.environment.unity_multi_vec_env import FlattenedVectorEnvThreaded
from unity_vecenv.environment.unity_vector_env import UnityVectorEnv


def make_unity_env_factory(
    *,
    base_port: int,
    scene_load: str,
    log_file_prefix: str,
    time_scale: int = 100,
    physics_steps_per_action: int = 10,
    no_graphics: bool = True,
) -> Callable[[Any], Any]:
    def make_env(args: Any):
        env = FlattenedVectorEnvThreaded(
            [
                lambda ind=i: UnityVectorEnv(
                    args.unity_env_path,
                    start_process=True,
                    port=base_port + ind,
                    time_scale=time_scale,
                    num_envs=args.num_envs,
                    physics_steps_per_action=physics_steps_per_action,
                    no_graphics=no_graphics,
                    scene_load=scene_load,
                    log_file=log_file_prefix,
                )
                for i in range(args.num_instances)
            ]
        )
        return RecordEpisodeStatistics(env)

    return make_env
