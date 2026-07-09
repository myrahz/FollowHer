using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace FollowHer.Features.Targeting.EntityInformation
{
    public class EntityInfo
    {
        private readonly Entity _entity;
        private readonly Life _life;
        private readonly GameController _gameController;

        public EntityInfo(Entity entity, GameController gameController)
        {
            _entity = entity;
            _gameController = gameController;
            _life = entity?.GetComponent<Life>();
        }

        public uint Id => _entity?.Id ?? 0;
        public string Path => _entity?.Path;
        public Vector3 Pos => _entity?.PosNum ?? Vector3.Zero;
        public Vector2 GridPos => _entity?.GridPosNum ?? Vector2.Zero;
        public float Distance => _entity?.DistancePlayer ?? float.MaxValue;

        public Vector2 ScreenPos => _entity != null ?
            _gameController.IngameState.Camera.WorldToScreen(_entity.PosNum) :
            Vector2.Zero;

        public bool IsValid => _entity?.IsValid ?? false;
        public bool IsAlive => _entity?.IsAlive ?? false;
        public bool IsTargetable => _entity?.IsTargetable ?? false;
        public bool IsHidden => _entity?.IsHidden ?? true;
        public bool IsHostile => _entity?.IsHostile ?? false;

        public float HPPercentage => _life?.HPPercentage ?? 0;
        public float ESPercentage => _life?.ESPercentage ?? 0;
        public MonsterRarity Rarity => _entity?.Rarity ?? MonsterRarity.White;

        public Entity Entity => _entity;
    }
}